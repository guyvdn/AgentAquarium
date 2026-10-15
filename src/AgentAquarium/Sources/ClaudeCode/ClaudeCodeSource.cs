using System.Text.Json;
using AgentAquarium.Domain;
using Microsoft.Extensions.Options;

namespace AgentAquarium.Sources.ClaudeCode;

public sealed class ClaudeCodeOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Folders holding per-project transcript dirs. Defaults to ~/.claude/projects.</summary>
    public IList<string> ProjectRoots { get; } = [];

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Only sessions written to within this window are picked up.</summary>
    public TimeSpan LookBack { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Claude Code's global config file, where it caches plan usage. Defaults to ~/.claude.json;
    /// a profile with its own CLAUDE_CONFIG_DIR keeps it at &lt;dir&gt;/.claude.json. Empty turns usage off.
    /// </summary>
    public string? UsageFile { get; set; }

    public TimeSpan UsagePollInterval { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Tails Claude Code transcripts. Layout, as of Claude Code 2.1:
/// <code>
/// &lt;root&gt;/&lt;project-slug&gt;/&lt;sessionId&gt;.jsonl                       main agent
/// &lt;root&gt;/&lt;project-slug&gt;/&lt;sessionId&gt;/subagents/agent-&lt;id&gt;.jsonl     subagent
/// &lt;root&gt;/&lt;project-slug&gt;/&lt;sessionId&gt;/subagents/agent-&lt;id&gt;.meta.json  its type, description, spawning tool_use id
/// </code>
/// Polls rather than using FileSystemWatcher: one directory listing per second is cheap,
/// and polling cannot silently drop events the way a watcher's buffer can.
/// </summary>
public sealed partial class ClaudeCodeSource(
    IAgentSink sink,
    IOptions<ClaudeCodeOptions> options,
    TimeProvider clock,
    ILogger<ClaudeCodeSource> logger) : BackgroundService
{
    public const string SourceName = "claude-code";

    private const int LiveMissesBeforeEnd = 3;

    private readonly ClaudeCodeOptions _options = options.Value;
    private readonly Dictionary<string, TrackedFile> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>tool_use id → the agent that issued it; how a subagent finds its parent.</summary>
    private readonly Dictionary<string, string> _toolUseOwner = [];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
            return;

        var roots = _options.ProjectRoots.Count > 0
            ? _options.ProjectRoots
            : [Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects")];
        LogWatching(logger, roots);

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

    private void Poll(IEnumerable<string> roots)
    {
        var since = clock.GetUtcNow() - _options.LookBack;

        foreach (var root in roots.Where(Directory.Exists))
        {
            // With the live-session registry, a session is in the tank exactly while its process
            // runs, however long it has been idle. Without it, fall back to transcript age.
            var live = LiveSessions.Read(root);
            PollRoot(root, live, since);
        }

        foreach (var file in _files.Values.ToList())
        {
            if (!_files.ContainsKey(file.Path))
                continue; // untracked earlier in this loop, together with its session

            // One locked or vanished file must not starve the others.
            try
            {
                ReadAppended(file);
                if (!file.IsSubagent)
                    ReportLiveStatus(file);
            }
            catch (FileNotFoundException)
            {
                if (file.IsSubagent)
                    _files.Remove(file.Path);
                else
                    Untrack(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogReadFailed(logger, file.Path, ex);
            }
        }

        // A nested subagent read before the subagent that spawned it was described under the
        // session; now that every file has been read, its real parent may be known.
        foreach (var file in _files.Values.Where(f => f.IsSubagent && !f.ParentResolved && f.Tail.Offset > 0))
            sink.Upsert(Describe(file));
    }

    private void PollRoot(string root, IReadOnlyDictionary<string, LiveSession>? live, DateTimeOffset since)
    {
        foreach (var projectDir in Directory.EnumerateDirectories(root))
            foreach (var main in new DirectoryInfo(projectDir).EnumerateFiles("*.jsonl"))
            {
                var sessionId = Path.GetFileNameWithoutExtension(main.Name);
                var tracked = _files.TryGetValue(main.FullName, out var existing);

                if (live is not null)
                {
                    if (!live.TryGetValue(sessionId, out var session))
                    {
                        // A pid file caught mid-write reads as missing; only a repeated miss ends a session.
                        if (tracked && ++existing!.LiveMisses >= LiveMissesBeforeEnd)
                            Untrack(existing);
                        continue;
                    }
                    if (tracked)
                    {
                        existing!.Live = session;
                        existing.LiveMisses = 0;
                    }
                }
                else if (main.LastWriteTimeUtc < since.UtcDateTime)
                {
                    if (tracked)
                        Untrack(existing!);
                    continue;
                }

                Track(main.FullName, () => new TrackedFile(main.FullName, sessionId, sessionId, null)
                {
                    Live = live?.GetValueOrDefault(sessionId),
                });

                var subagentDir = new DirectoryInfo(Path.Combine(projectDir, sessionId, "subagents"));
                if (!subagentDir.Exists)
                    continue;
                foreach (var sub in subagentDir.EnumerateFiles("agent-*.jsonl"))
                {
                    var agentId = Path.GetFileNameWithoutExtension(sub.Name)["agent-".Length..];
                    Track(sub.FullName, () => new TrackedFile(sub.FullName, $"{sessionId}/{agentId}", sessionId,
                        Path.ChangeExtension(sub.FullName, ".meta.json")));
                }
            }
    }

    /// <summary>The session's process ended: drop its fish and stop tailing its files.</summary>
    private void Untrack(TrackedFile main)
    {
        foreach (var file in _files.Values.Where(f => f.SessionId == main.SessionId).ToList())
            _files.Remove(file.Path);
        foreach (var (toolUseId, _) in _toolUseOwner.Where(o => SessionOf(o.Value) == main.SessionId).ToList())
            _toolUseOwner.Remove(toolUseId);
        sink.Remove(main.AgentId);
        LogSessionEnded(logger, main.SessionId);
    }

    /// <summary>The registry's busy/idle is authoritative and catches what transcripts miss (e.g. an interrupted turn).</summary>
    private void ReportLiveStatus(TrackedFile file)
    {
        // Only once the agent exists in the tank; before that the observation would be dropped.
        if (file.Live?.Status is not { } status || file.Tail.Offset == 0 || status == file.ReportedLiveStatus)
            return;

        AgentStatus? mapped = status switch
        {
            "busy" => AgentStatus.Working,
            "idle" => AgentStatus.Idle,
            _ => null,
        };
        if (mapped is null)
            return;

        var at = file.Live.StatusUpdatedAt is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : clock.GetUtcNow();
        sink.Observe(file.AgentId, new StatusObserved(at, mapped.Value));
        file.ReportedLiveStatus = status;
    }

    private void Track(string path, Func<TrackedFile> create)
    {
        if (!_files.ContainsKey(path))
            _files[path] = create();
    }

    private void ReadAppended(TrackedFile file)
    {
        // Chunked, so a multi-MB transcript replayed at startup never sits in memory whole.
        do
        {
            var lines = file.Tail.ReadNewLines();
            if (lines.Count > 0)
                Apply(file, lines);
        } while (file.Tail.Behind);
    }

    private void Apply(TrackedFile file, IReadOnlyList<string> lines)
    {
        var parsed = lines.Select(TranscriptParser.Parse).ToList();

        foreach (var line in parsed)
        {
            file.Cwd = line.Cwd ?? file.Cwd;
            file.GitBranch = line.GitBranch ?? file.GitBranch;
            file.Title = line.Title ?? file.Title;
            file.Entrypoint = line.Entrypoint ?? file.Entrypoint;
            foreach (var id in line.ToolUseIds)
                _toolUseOwner[id] = file.AgentId;
        }

        // Upsert before observing: the registry ignores observations for unknown agents,
        // and may have forgotten this one if it went quiet and has now woken up.
        sink.Upsert(Describe(file));

        foreach (var observation in parsed.SelectMany(l => l.Observations))
            sink.Observe(file.AgentId, file.IsSubagent ? EndTurnMeansDone(observation) : observation);

        // Transcript lines may have set a status; re-apply the registry's, which wins.
        file.ReportedLiveStatus = null;
    }

    private AgentDescriptor Describe(TrackedFile file)
    {
        if (!file.IsSubagent)
        {
            var project = file.Cwd is null ? file.SessionId[..Math.Min(8, file.SessionId.Length)] : ProjectName(file.Cwd);
            var host = Host(file.Live?.Entrypoint ?? file.Entrypoint, file.Cwd);
            var detail = string.Join(" · ", new[] { host, file.Title, file.GitBranch }.Where(s => !string.IsNullOrEmpty(s)));
            return new AgentDescriptor(file.AgentId, null, file.SessionId, SourceName, project, detail, 0,
                LifetimeManaged: file.Live is not null);
        }

        file.Meta ??= SubagentMeta.TryRead(file.MetaPath!);
        var meta = file.Meta;
        string? owner = null;
        file.ParentResolved = meta?.ToolUseId is { } toolUseId && _toolUseOwner.TryGetValue(toolUseId, out owner);
        var parent = file.ParentResolved ? owner! : file.SessionId;
        return new AgentDescriptor(file.AgentId, parent, file.SessionId, SourceName,
            meta?.AgentType ?? "subagent", meta?.Description, meta?.SpawnDepth ?? 1);
    }

    /// <summary>
    /// The repo a session works on. Worktree folders have throwaway names, so look past them:
    /// <c>repo/.claude/worktrees/x</c> → repo; <c>~/.t3/worktrees/repo/x</c> → repo.
    /// </summary>
    internal static string ProjectName(string cwd)
    {
        var parts = cwd.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        var i = Array.FindLastIndex(parts, p => p.Equals("worktrees", StringComparison.OrdinalIgnoreCase));
        if (i > 1 && parts[i - 1] is ".claude" or ".git")
            return parts[i - 2];
        if (i >= 0 && i + 1 < parts.Length)
            return parts[i + 1];
        return parts.Length > 0 ? parts[^1] : cwd;
    }

    /// <summary>What the session runs in. <c>sdk-ts</c> is any Agent SDK host; T3 Code is recognised by its worktree folder.</summary>
    internal static string? Host(string? entrypoint, string? cwd) => entrypoint switch
    {
        _ when cwd?.Contains($"{Path.DirectorySeparatorChar}.t3{Path.DirectorySeparatorChar}", StringComparison.Ordinal) == true
               || cwd?.Contains("/.t3/", StringComparison.Ordinal) == true => "T3 Code",
        "cli" => "CLI",
        "sdk-ts" or "sdk-py" => "SDK",
        null or "" => null,
        _ => entrypoint,
    };

    [LoggerMessage(Level = LogLevel.Information, Message = "Watching Claude Code transcripts in {Roots}")]
    private static partial void LogWatching(ILogger logger, IEnumerable<string> roots);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Polling Claude Code transcripts failed; retrying next tick")]
    private static partial void LogPollFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not read {Path}; retrying next tick")]
    private static partial void LogReadFailed(ILogger logger, string path, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Session {Session} ended")]
    private static partial void LogSessionEnded(ILogger logger, string session);

    /// <summary>Agent ids are <c>session</c> or <c>session/agent</c>.</summary>
    private static string SessionOf(string agentId) =>
        agentId.IndexOf('/', StringComparison.Ordinal) is var slash and >= 0 ? agentId[..slash] : agentId;

    private static AgentObservation EndTurnMeansDone(AgentObservation o) =>
        o is StatusObserved { Status: AgentStatus.Idle } s ? s with { Status = AgentStatus.Done } : o;

    private sealed class TrackedFile(string path, string agentId, string sessionId, string? metaPath)
    {
        public JsonlTail Tail { get; } = new(path);
        public string Path => Tail.Path;
        public string AgentId { get; } = agentId;
        public string SessionId { get; } = sessionId;
        public string? MetaPath { get; } = metaPath;
        public bool IsSubagent => MetaPath is not null;

        public string? Cwd { get; set; }
        public string? GitBranch { get; set; }
        public string? Title { get; set; }
        public string? Entrypoint { get; set; }
        public SubagentMeta? Meta { get; set; }
        public bool ParentResolved { get; set; }

        /// <summary>Registry entry of the running process, when the registry exists.</summary>
        public LiveSession? Live { get; set; }

        /// <summary>Consecutive polls in which the registry did not list this session.</summary>
        public int LiveMisses { get; set; }

        public string? ReportedLiveStatus { get; set; }
    }

    private sealed record SubagentMeta(string? AgentType, string? Description, string? ToolUseId, int? SpawnDepth)
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

        public static SubagentMeta? TryRead(string path)
        {
            try
            {
                return File.Exists(path) ? JsonSerializer.Deserialize<SubagentMeta>(File.ReadAllText(path), Json) : null;
            }
            catch (Exception ex) when (ex is IOException or JsonException)
            {
                return null;
            }
        }
    }
}
