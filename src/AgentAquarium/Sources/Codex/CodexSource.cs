using AgentAquarium.Domain;
using AgentAquarium.Sources.ClaudeCode;
using Microsoft.Extensions.Options;

namespace AgentAquarium.Sources.Codex;

public sealed class CodexOptions
{
    public bool Enabled { get; set; } = true;
    public IList<string> SessionRoots { get; } = [];
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan LookBack { get; set; } = TimeSpan.FromMinutes(30);
    public string? T3StateFile { get; set; }
    public TimeSpan T3PollInterval { get; set; } = TimeSpan.FromSeconds(5);
}

/// <summary>Tails Codex rollout logs; writer locks keep quiet, open sessions in the tank.</summary>
public sealed partial class CodexSource(
    IAgentSink sink,
    IUsageSink usage,
    IOptions<CodexOptions> options,
    TimeProvider clock,
    ILogger<CodexSource> logger) : BackgroundService
{
    public const string SourceName = "codex";
    private readonly CodexOptions _options = options.Value;
    private readonly Dictionary<string, TrackedSession> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly T3ThreadTitles _titles = new(options.Value.T3StateFile, "codex");
    private DateTimeOffset? _titlesReadAt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
            return;
        var home = Environment.GetEnvironmentVariable("CODEX_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
        var roots = _options.SessionRoots.Count > 0 ? _options.SessionRoots : [Path.Combine(home, "sessions")];
        using var timer = new PeriodicTimer(_options.PollInterval, clock);
        do
        {
            try
            {
                Poll(roots);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogPollFailed(logger, ex);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal void Poll(IEnumerable<string> roots)
    {
        if (_titlesReadAt is null || clock.GetUtcNow() - _titlesReadAt >= _options.T3PollInterval)
        {
            _titles.Refresh();
            _titlesReadAt = clock.GetUtcNow();
        }
        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var path in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
            {
                try
                {
                    PollFile(path, root);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LogReadFailed(logger, path, ex);
                }
            }
        }
        foreach (var (path, file) in _files.ToList())
            if (!File.Exists(path))
            {
                _files.Remove(path);
                if (file.State.Id is { } id)
                    sink.Remove(Id(id));
            }
    }

    private void PollFile(string path, string root)
    {
        var recent = File.GetLastWriteTimeUtc(path) >= (clock.GetUtcNow() - _options.LookBack).UtcDateTime;
        // The UUID is the final 36 characters of rollout-<timestamp>-<uuid>.jsonl.
        var name = Path.GetFileNameWithoutExtension(path);
        var nativeId = name.Length >= 36 ? name[^36..] : name;
        var live = WriterIsLive(Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(root))!,
            "thread-writer-locks", nativeId + ".lock"));
        if (live != true && !recent)
        {
            if (_files.Remove(path, out var old) && old.State.Id is { } oldId)
                sink.Remove(Id(oldId));
            return;
        }
        if (!_files.TryGetValue(path, out var file))
        {
            file = new TrackedSession(path);
            _files.Add(path, file);
        }
        // A closed session's log was still consumed for usage. On resume, rebuild the registry's
        // context and counters from history, suppressing spend throughout the replay.
        if (file.Closed && live != false)
        {
            file = new TrackedSession(path);
            _files[path] = file;
        }
        var replaying = file.Replaying;
        var observations = new List<AgentObservation>();
        foreach (var line in file.Tail.ReadNewLines())
            observations.AddRange(file.State.Read(line));
        file.Replaying = file.Tail.Behind;
        var state = file.State;
        if (state.Usage is { } report)
            usage.Report(report);
        if (live == false)
        {
            if (file.Published && state.Id is { } endedId)
                sink.Remove(Id(endedId));
            file.Published = false;
            file.Closed = true;
            return;
        }
        Publish(file, live, observations, replaying);
    }

    private void Publish(TrackedSession file, bool? live, List<AgentObservation> observations, bool replaying)
    {
        var state = file.State;
        if (state.Id is not { } id || state.Internal)
            return;
        // A completed child must not be resurrected after DoneLinger has removed it.
        if (state.ParentId is not null && state.Status == AgentStatus.Done && file.Published && observations.Count == 0)
            return;
        var title = _titles.TitleFor(id);
        var project = state.Cwd is { } cwd ? ClaudeCodeSource.ProjectName(cwd) : id[..Math.Min(8, id.Length)];
        var host = title is not null ? "T3 Code · Codex" : state.Originator == "T3 Code" ? "T3 Code · Codex" : "Codex";
        var detail = string.Join(" · ", new[] { host, title is null ? null : project, state.Branch is "HEAD" ? null : state.Branch }
            .Where(s => !string.IsNullOrEmpty(s)));
        var parent = state.ParentId;
        // Follow the known parent chain instead of relying on enumeration order.
        var session = id;
        var depth = 0;
        var seen = new HashSet<string>(StringComparer.Ordinal) { id };
        while (parent is not null && seen.Add(parent))
        {
            depth++;
            session = parent;
            parent = _files.Values.FirstOrDefault(f => f.State.Id == parent)?.State.ParentId;
        }
        sink.Upsert(new AgentDescriptor(Id(id), state.ParentId is { } p ? Id(p) : null, Id(session), SourceName,
            title ?? state.Title ?? project, detail, depth, LifetimeManaged: live == true,
            WorkInFlight: state.OpenCalls.Count > 0));
        file.Published = true;
        foreach (var observation in observations)
            if (!replaying || observation is not SpendObserved)
                sink.Observe(Id(id), observation);
    }

    internal static string Id(string nativeId) => "codex/" + nativeId;

    /// <summary>Null means this Codex version/platform has no usable writer-lock signal.</summary>
    internal static bool? WriterIsLive(string path)
    {
        if (!File.Exists(path) || OperatingSystem.IsMacOS())
            return null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            try
            {
                stream.Lock(0, 1);
            }
            catch (IOException)
            {
                return true;
            }
            stream.Unlock(0, 1);
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private sealed class TrackedSession(string path)
    {
        public JsonlTail Tail { get; } = new(path);
        public CodexTranscript State { get; } = new();
        public bool Published { get; set; }
        public bool Replaying { get; set; } = true;
        public bool Closed { get; set; }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Polling Codex sessions failed; retrying next tick")]
    private static partial void LogPollFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not read Codex transcript {Path}; retrying next tick")]
    private static partial void LogReadFailed(ILogger logger, string path, Exception exception);
}
