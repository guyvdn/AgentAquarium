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

    /// <summary>
    /// Fallback when the live-session registry is missing (older Claude Code): sessions whose
    /// transcript changed within this window are shown instead.
    /// </summary>
    public TimeSpan LookBack { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Claude Code's global config file, where it caches plan usage. Defaults to ~/.claude.json;
    /// a profile with its own CLAUDE_CONFIG_DIR keeps it at &lt;dir&gt;/.claude.json. Empty turns usage off.
    /// </summary>
    public string? UsageFile { get; set; }

    public TimeSpan UsagePollInterval { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// T3 Code's state database, for the titles it gives its threads. Defaults to ~/.t3/userdata/state.sqlite.
    /// Empty turns it off, and T3 Code sessions show Claude's title.
    /// </summary>
    public string? T3StateFile { get; set; }

    public TimeSpan T3PollInterval { get; set; } = TimeSpan.FromSeconds(5);
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
    PermissionRequests permissionRequests,
    TimeProvider clock,
    ILogger<ClaudeCodeSource> logger) : BackgroundService
{
    public const string SourceName = "claude-code";

    private const int LiveMissesBeforeEnd = 3;

    private const string T3Host = "T3 Code";

    /// <summary>
    /// How long a reported permission prompt may wait for its tool call to be read from the transcript.
    /// The call is written before the prompt, so this only covers the poll interval; past it, it was answered.
    /// </summary>
    private static readonly TimeSpan PermissionMatchWindow = TimeSpan.FromSeconds(10);

    private readonly ClaudeCodeOptions _options = options.Value;
    private readonly Dictionary<string, TrackedFile> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>tool_use id → the agent that issued it; how a subagent finds its parent.</summary>
    private readonly Dictionary<string, string> _toolUseOwner = [];

    /// <summary>Permission prompts from the hook whose tool call has not been read yet.</summary>
    private readonly List<PermissionRequest> _permissionRequests = [];

    private readonly T3ThreadTitles _t3Titles = new(options.Value.T3StateFile ?? T3ThreadTitles.DefaultStateFile);
    private DateTimeOffset? _t3TitlesReadAt;

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

    internal void Poll(IEnumerable<string> roots)
    {
        var since = clock.GetUtcNow() - _options.LookBack;
        var t3TitlesChanged = RefreshT3Titles();

        foreach (var root in roots.Where(Directory.Exists))
        {
            // With the live-session registry, a session is in the tank exactly while its process
            // runs, however long it has been idle. Without it, fall back to transcript age.
            var live = LiveSessions.Read(root);
            PollRoot(root, live, since);
        }

        // Main transcripts first, so a subagent's parent is known when it is read. Insertion order is no
        // guide: once a session has been untracked, the dictionary reuses its slots in any order.
        foreach (var file in _files.Values.OrderBy(f => f.IsSubagent).ToList())
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
        // session; now that every file has been read, its real parent may be known. Not once it has
        // finished: the tank may already have let it go, and describing it again would bring it back.
        // A finished nested one read before its parent so keeps the session as parent while it lingers.
        foreach (var file in _files.Values.Where(f => f.IsSubagent && !f.ParentResolved && !f.Finished && f.Tail.Offset > 0))
            sink.Upsert(Describe(file));

        // A thread renamed in T3 Code while its session is quiet writes nothing to the transcript. Only sessions
        // the registry keeps while their process runs: one the tank has pruned would come back.
        if (t3TitlesChanged)
            foreach (var file in _files.Values.Where(f => !f.IsSubagent && f.Live is not null && f.Tail.Offset > 0))
                sink.Upsert(Describe(file));

        _permissionRequests.AddRange(permissionRequests.Drain());
        _permissionRequests.RemoveAll(MarkPermissionWait);
    }

    /// <summary>
    /// Turns a permission prompt into a wait on the tool call it is about, in the agent that asked. From
    /// there the wait ends like any other, when the call's result or the end of the turn is read: no hook
    /// fires when a prompt is approved, so an approved call stays waiting until it has run. The CLI's
    /// registry does see approvals, so CLI sessions are left to it. False to retry next poll.
    /// </summary>
    private bool MarkPermissionWait(PermissionRequest request)
    {
        if (_files.Values.FirstOrDefault(f => f.AgentId == request.SessionId)?.Live?.Entrypoint == "cli")
            return true;

        // A subagent's request carries its agent_id; without one, the main agent asked.
        var agentId = request.AgentId is { Length: > 0 } a ? $"{request.SessionId}/{a}" : request.SessionId;
        if (_files.Values.FirstOrDefault(f => f.AgentId == agentId) is { } file
            && PickToolCall(file.OpenToolUses.Where(u => !file.Waits.ContainsKey(u.Key)), request) is { } id)
        {
            var reason = $"permission for {request.ToolName}";
            file.Waits[id] = reason;
            sink.Observe(file.AgentId, new StatusObserved(request.At, AgentStatus.Waiting, reason));
            return true;
        }
        return clock.GetUtcNow() - request.At > PermissionMatchWindow;
    }

    /// <summary>
    /// The open call a permission prompt is about: of that tool, made before the prompt, with the same
    /// arguments (parallel calls of one tool), else the only one. When it is not clear, none: a missed
    /// prompt costs less than marking a running call as waiting.
    /// </summary>
    internal static string? PickToolCall(IEnumerable<KeyValuePair<string, OpenToolUse>> open, PermissionRequest request)
    {
        var candidates = open
            .Where(u => u.Value.Name == request.ToolName && u.Value.At <= request.At)
            .ToList();
        var exact = request.Input is { } input
            ? candidates.Find(u => u.Value.Input.ValueKind != JsonValueKind.Undefined && JsonElement.DeepEquals(u.Value.Input, input))
            : default;
        return exact.Key ?? (candidates.Count == 1 ? candidates[0].Key : null);
    }

    private bool RefreshT3Titles()
    {
        var now = clock.GetUtcNow();
        if (now - _t3TitlesReadAt < _options.T3PollInterval)
            return false;
        _t3TitlesReadAt = now;
        return _t3Titles.Refresh();
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

    /// <summary>
    /// The registry's status is authoritative and catches what transcripts miss (an interrupted turn,
    /// a permission prompt in the CLI), except for a wait still open: a question in the transcript, or a
    /// permission prompt reported by the hook.
    /// </summary>
    private void ReportLiveStatus(TrackedFile file)
    {
        // Only once the agent exists in the tank; before that the observation would be dropped.
        if (file.Live?.Status is not { } status || file.Tail.Offset == 0 || status == file.ReportedLiveStatus)
            return;

        AgentStatus? mapped = status switch
        {
            // An SDK host asks its questions itself, so the registry cannot see them; the transcript can.
            _ when file.Waits.Count > 0 => AgentStatus.Waiting,
            "busy" => AgentStatus.Working,
            "idle" => AgentStatus.Idle,
            "waiting" => AgentStatus.Waiting,
            _ => null,
        };
        if (mapped is null)
            return;

        var at = file.Live.StatusUpdatedAt is { } ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : clock.GetUtcNow();
        var reason = file.Live.WaitingFor ?? file.Waits.Values.FirstOrDefault();
        sink.Observe(file.AgentId, new StatusObserved(at, mapped.Value, reason));
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
        var observations = new List<AgentObservation>();

        foreach (var line in lines.Select(TranscriptParser.Parse))
        {
            file.Cwd = line.Cwd ?? file.Cwd;
            file.GitBranch = line.GitBranch ?? file.GitBranch;
            file.Title = line.Title ?? file.Title;
            file.Entrypoint = line.Entrypoint ?? file.Entrypoint;
            foreach (var use in line.ToolUses)
            {
                _toolUseOwner[use.Id] = file.AgentId;
                file.OpenToolUses[use.Id] = new OpenToolUse(use.Name, line.At ?? clock.GetUtcNow(), use.Input);
            }
            foreach (var id in line.ToolResultIds)
                file.OpenToolUses.Remove(id);
            // An interrupted turn can leave calls without a result; the next prompt closes them.
            if (EndsTurn(line) || line.IsPrompt)
                file.OpenToolUses.Clear();

            TrackWaits(file.Waits, line, observations);
        }

        // Upsert before observing: the registry ignores observations for unknown agents,
        // and may have forgotten this one if it went quiet and has now woken up.
        sink.Upsert(Describe(file));

        foreach (var observation in observations.Select(o => file.IsSubagent ? EndTurnMeansDone(o) : o))
        {
            sink.Observe(file.AgentId, observation);
            if (observation is StatusObserved s)
                file.Finished = s.Status == AgentStatus.Done;
        }

        // Transcript lines may have set a status; re-apply the registry's, which wins.
        file.ReportedLiveStatus = null;
    }

    /// <summary>
    /// Adds a line's observations, keeping track of the questions the agent has asked and not yet
    /// had answered. The answer is a plain tool result, which reports no status of its own, so the
    /// turn resuming is reported here.
    /// </summary>
    internal static void TrackWaits(Dictionary<string, string> waits, TranscriptLine line, List<AgentObservation> observations)
    {
        // The turn ended or a new one began, answered or not (a result we never saw).
        if (EndsTurn(line) || line.IsPrompt)
            waits.Clear();
        foreach (var wait in line.Waits)
            waits[wait.ToolUseId] = wait.Reason;

        var answered = false;
        foreach (var id in line.ToolResultIds)
            answered |= waits.Remove(id);

        // A tool called alongside the question (a later line of the same message) does not answer it.
        foreach (var observation in line.Observations)
            observations.Add(observation is StatusObserved { Status: AgentStatus.Working } && waits.Count > 0
                ? new StatusObserved(observation.At, AgentStatus.Waiting, waits.Values.First())
                : observation);

        if (answered && waits.Count == 0 && line.At is { } at)
            observations.Add(new StatusObserved(at, AgentStatus.Working));
    }

    private static bool EndsTurn(TranscriptLine line) =>
        line.Observations.OfType<StatusObserved>().Any(s => s.Status is AgentStatus.Idle or AgentStatus.Done);

    private AgentDescriptor Describe(TrackedFile file)
    {
        if (!file.IsSubagent)
        {
            var project = file.Cwd is null ? file.SessionId[..Math.Min(8, file.SessionId.Length)] : ProjectName(file.Cwd);
            var host = Host(file.Live?.Entrypoint ?? file.Entrypoint, file.Cwd);
            // T3 Code names its threads itself; show the name its sidebar shows.
            var title = host == T3Host ? _t3Titles.TitleFor(file.SessionId) ?? file.Title : file.Title;
            // The title is what tells sessions apart; the project goes with it once there is one.
            var label = string.IsNullOrEmpty(title) ? project : title;
            var where = string.IsNullOrEmpty(title) || file.Cwd is null ? null : project;
            // HEAD is no branch: a detached checkout, or a folder that is not a repo at all.
            var branch = file.GitBranch is "HEAD" ? null : file.GitBranch;
            var detail = string.Join(" · ", new[] { host, where, branch }.Where(s => !string.IsNullOrEmpty(s)));
            return new AgentDescriptor(file.AgentId, null, file.SessionId, SourceName, label, detail, 0,
                LifetimeManaged: file.Live is not null);
        }

        file.Meta ??= SubagentMeta.TryRead(file.MetaPath!);
        var meta = file.Meta;
        string? owner = null;
        file.ParentResolved = meta?.ToolUseId is { } toolUseId && _toolUseOwner.TryGetValue(toolUseId, out owner);
        var parent = file.ParentResolved ? owner! : file.SessionId;
        // Like a session's title, the task tells subagents apart; most share a type.
        var type = meta?.AgentType ?? "subagent";
        var (name, below) = string.IsNullOrEmpty(meta?.Description) ? (type, null) : (meta.Description, type);
        return new AgentDescriptor(file.AgentId, parent, file.SessionId, SourceName, name, below, meta?.SpawnDepth ?? 1);
    }

    /// <summary>
    /// The repo a session works on. Worktree folders have throwaway names, so look past them:
    /// <c>repo/.claude/worktrees/x</c> → repo; <c>~/.t3/worktrees/repo/x</c> → repo. A T3 Code thread
    /// outside any repo gets a folder of its own: <c>~/.t3/scratch/&lt;date&gt;-&lt;slug&gt;</c> → scratch.
    /// </summary>
    internal static string ProjectName(string cwd)
    {
        var parts = cwd.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        for (var s = 1; s < parts.Length - 1; s++)
            if (parts[s].Equals("scratch", StringComparison.OrdinalIgnoreCase)
                && parts[s - 1].Equals(".t3", StringComparison.OrdinalIgnoreCase))
                return "scratch";
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
               || cwd?.Contains("/.t3/", StringComparison.Ordinal) == true => T3Host,
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

        /// <summary>A subagent whose last reported status is Done.</summary>
        public bool Finished { get; set; }

        /// <summary>Registry entry of the running process, when the registry exists.</summary>
        public LiveSession? Live { get; set; }

        /// <summary>Consecutive polls in which the registry did not list this session.</summary>
        public int LiveMisses { get; set; }

        public string? ReportedLiveStatus { get; set; }

        /// <summary>Open questions to the user: tool_use id → what for.</summary>
        public Dictionary<string, string> Waits { get; } = new(StringComparer.Ordinal);

        /// <summary>Tool calls without a result yet, in this turn, by tool_use id.</summary>
        public Dictionary<string, OpenToolUse> OpenToolUses { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>A tool call without a result yet: what, when, and with which arguments.</summary>
    internal sealed record OpenToolUse(string Name, DateTimeOffset At, JsonElement Input);

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
