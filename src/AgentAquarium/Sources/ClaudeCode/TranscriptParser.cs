using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using AgentAquarium.Domain;

namespace AgentAquarium.Sources.ClaudeCode;

/// <summary>What one transcript line tells us, already in source-neutral terms.</summary>
public sealed record TranscriptLine(
    IReadOnlyList<AgentObservation> Observations,
    IReadOnlyList<ToolUse> ToolUses,
    string? Cwd = null,
    string? GitBranch = null,
    string? Title = null,
    string? Entrypoint = null)
{
    public static readonly TranscriptLine Empty = new([], []);

    public DateTimeOffset? At { get; init; }

    /// <summary>Tool calls that block on the user until their result comes back.</summary>
    public IReadOnlyList<UserWait> Waits { get; init; } = [];

    /// <summary>The tool calls this line carries results for.</summary>
    public IReadOnlyList<string> ToolResultIds { get; init; } = [];

    /// <summary>A prompt from the user: whatever the turn before left open is over.</summary>
    public bool IsPrompt { get; init; }

    /// <summary>Work started in the background: a command, a subagent, a monitor.</summary>
    public IReadOnlyList<BackgroundTask> BackgroundStarted { get; init; } = [];

    /// <summary>Background work that reported it has ended, by task id.</summary>
    public IReadOnlyList<string> BackgroundEnded { get; init; } = [];
}

/// <summary>Work running in the background; <paramref name="Timeout"/> when it says how long it may run (a monitor).</summary>
public sealed record BackgroundTask(string Id, TimeSpan? Timeout = null);

/// <summary>A tool call the agent made, with its arguments.</summary>
public sealed record ToolUse(string Id, string Name, JsonElement Input);

/// <summary>A tool call waiting on the user, and what for.</summary>
public sealed record UserWait(string ToolUseId, string Reason);

/// <summary>
/// Parses one line of a Claude Code session transcript (<c>~/.claude/projects/&lt;slug&gt;/*.jsonl</c>).
/// The format is undocumented and evolves, so everything is read defensively: unknown
/// record types and missing fields are ignored rather than failing the line.
/// </summary>
public static class TranscriptParser
{
    /// <summary>A background subagent ends by calling this tool, not with an end_turn.</summary>
    private const string HandbackTool = "SubagentHandback";

    /// <summary>
    /// Tools that are answered by the user, not run. An SDK host (T3 Code) never reports
    /// "waiting" in the live-session registry, so for its sessions these are the only sign.
    /// </summary>
    private static readonly Dictionary<string, string> UserTools = new(StringComparer.Ordinal)
    {
        ["AskUserQuestion"] = "a question",
        ["ExitPlanMode"] = "plan approval",
    };

    public static TranscriptLine Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return TranscriptLine.Empty;

        try
        {
            using var doc = JsonDocument.Parse(line);
            return Parse(doc.RootElement);
        }
        catch (JsonException)
        {
            return TranscriptLine.Empty;
        }
    }

    private static TranscriptLine Parse(JsonElement root)
    {
        var type = Str(root, "type");
        if (type == "ai-title")
            return TranscriptLine.Empty with { Title = Str(root, "aiTitle") };

        if (!root.TryGetProperty("timestamp", out var ts) || !ts.TryGetDateTimeOffset(out var at))
            return TranscriptLine.Empty;

        var observations = new List<AgentObservation>();
        var toolUses = new List<ToolUse>();
        var waits = new List<UserWait>();
        var toolResultIds = new List<string>();
        var isPrompt = false;
        IReadOnlyList<BackgroundTask> backgroundStarted = [];
        IReadOnlyList<string> backgroundEnded = [];

        switch (type)
        {
            case "assistant" when root.TryGetProperty("message", out var message):
                ParseAssistant(message, at, observations, toolUses, waits);
                break;
            case "user" when root.TryGetProperty("message", out var message):
                // Not text Claude Code adds itself mid-turn (a skill's body, a command caveat), nor text beside a result.
                isPrompt = ParseUser(message, at, observations, toolResultIds) && toolResultIds.Count == 0
                           && !(root.TryGetProperty("isMeta", out var isMeta) && isMeta.ValueKind == JsonValueKind.True);
                backgroundStarted = BackgroundStart(root);
                backgroundEnded = [.. BackgroundEnd(UserText(message)), .. Stopped(root)];
                break;
            // A notification that arrives mid-turn is queued, and written as an attachment.
            case "attachment" when root.TryGetProperty("attachment", out var attachment)
                                   && Str(attachment, "type") == "queued_command":
                backgroundEnded = BackgroundEnd(Str(attachment, "prompt") ?? "");
                break;
            case "system" when Str(root, "subtype") == "compact_boundary":
                var meta = root.TryGetProperty("compactMetadata", out var m) ? m : default;
                observations.Add(new CompactionObserved(at, Long(meta, "preTokens"), Long(meta, "postTokens")));
                break;
        }

        return new TranscriptLine(observations, toolUses, Str(root, "cwd"), Str(root, "gitBranch"),
            Entrypoint: Str(root, "entrypoint"))
        {
            At = at,
            Waits = waits,
            ToolResultIds = toolResultIds,
            IsPrompt = isPrompt,
            BackgroundStarted = backgroundStarted,
            BackgroundEnded = backgroundEnded,
        };
    }

    /// <summary>
    /// What a tool result says now runs in the background. Each kind words it differently: a Bash
    /// command <c>backgroundTaskId</c>; an Agent <c>status: async_launched</c> with its <c>agentId</c>;
    /// a Monitor <c>taskId</c> with its <c>timeoutMs</c>.
    /// </summary>
    private static IReadOnlyList<BackgroundTask> BackgroundStart(JsonElement root)
    {
        if (!root.TryGetProperty("toolUseResult", out var result) || result.ValueKind != JsonValueKind.Object)
            return [];
        if ((Str(result, "backgroundTaskId") ?? (Str(result, "status") == "async_launched" ? Str(result, "agentId") : null)) is { } id)
            return [new BackgroundTask(id)];
        if (Str(result, "taskId") is { } monitor && Long(result, "timeoutMs") is > 0 and var ms)
            return [new BackgroundTask(monitor, TimeSpan.FromMilliseconds(ms))];
        return [];
    }

    /// <summary>A TaskStop result: the task it stopped sends no notification of its own.</summary>
    private static IReadOnlyList<string> Stopped(JsonElement root) =>
        root.TryGetProperty("toolUseResult", out var result) && Str(result, "task_type") is not null
        && Str(result, "task_id") is { } id
            ? [id]
            : [];

    /// <summary>
    /// Background work that has ended, from what Claude Code delivers back to the session: a
    /// <c>&lt;task-notification&gt;</c> with a final status (a monitor's events have none), or a
    /// subagent's hand-back, which many send instead of a notification.
    /// </summary>
    internal static List<string> BackgroundEnd(string text)
    {
        var ended = new List<string>();
        foreach (var notification in Between(text, "<task-notification>", "</task-notification>"))
            if (Between(notification, "<task-id>", "</task-id>").FirstOrDefault() is { } id
                && Between(notification, "<status>", "</status>").FirstOrDefault() is "completed" or "failed" or "killed" or "stopped")
                ended.Add(id);
        foreach (var message in Between(text, "<agent-message from=\"", "</agent-message>"))
            if (message.IndexOf("\">", StringComparison.Ordinal) is > 0 and var quote
                && message[(quote + 2)..].TrimStart().StartsWith("[Subagent hand-back]", StringComparison.Ordinal))
                ended.Add(message[..quote]);
        return ended;
    }

    /// <summary>Every piece of text between an opening and a closing marker, never crossing into the next.</summary>
    private static IEnumerable<string> Between(string text, string open, string close)
    {
        for (var start = text.IndexOf(open, StringComparison.Ordinal); start >= 0;
             start = text.IndexOf(open, start + open.Length, StringComparison.Ordinal))
        {
            var from = start + open.Length;
            var end = text.IndexOf(close, from, StringComparison.Ordinal);
            if (end < 0)
                yield break;
            yield return text[from..end];
        }
    }

    private static string UserText(JsonElement message) =>
        message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
            ? content.GetString()!
            : string.Concat(Blocks(message).Select(b => Str(b, "type") == "text" ? Str(b, "text") : null));

    private static void ParseAssistant(JsonElement message, DateTimeOffset at,
        List<AgentObservation> observations, List<ToolUse> toolUses, List<UserWait> waits)
    {
        if (message.TryGetProperty("usage", out var usage))
        {
            var tokens = Long(usage, "input_tokens")
                         + Long(usage, "cache_read_input_tokens")
                         + Long(usage, "cache_creation_input_tokens")
                         + Long(usage, "output_tokens");
            if (tokens > 0)
                observations.Add(new ContextObserved(at, tokens));
            if (Spend(usage) is > 0 and var spent)
                observations.Add(new SpendObserved(at, Str(message, "id"), spent));
        }

        var handedBack = false;
        foreach (var block in Blocks(message))
        {
            if (Str(block, "type") != "tool_use")
                continue;
            var name = Str(block, "name") ?? "tool";
            observations.Add(new ToolCallObserved(at, name));
            handedBack |= name == HandbackTool;
            if (Str(block, "id") is not { } id)
                continue;
            toolUses.Add(new ToolUse(id, name, block.TryGetProperty("input", out var input) ? input.Clone() : default));
            if (UserTools.TryGetValue(name, out var reason))
                waits.Add(new UserWait(id, reason));
        }

        // null stop_reason = a partial (streamed) record; the turn is still going.
        var stop = Str(message, "stop_reason");
        var working = !handedBack && stop is null or "tool_use" or "pause_turn";
        observations.Add(waits.Count > 0
            ? new StatusObserved(at, AgentStatus.Waiting, waits[0].Reason)
            : new StatusObserved(at, working ? AgentStatus.Working : AgentStatus.Idle));
    }

    /// <summary>
    /// Input-equivalent tokens, with API pricing's ratios: a cache read is a tenth of fresh input,
    /// a cache write 1.25× (5-minute) or 2× (1-hour), output 5×. The model's own price is left out.
    /// </summary>
    internal static long Spend(JsonElement usage)
    {
        var written = Long(usage, "cache_creation_input_tokens");
        // The total wins over the split: a /compact summary line has a split under a total of 0.
        var oneHour = usage.TryGetProperty("cache_creation", out var split)
            ? Math.Min(written, Long(split, "ephemeral_1h_input_tokens"))
            : 0;
        var fiveMinute = written - oneHour;
        return (long)(Long(usage, "input_tokens") + 1.25 * fiveMinute + 2 * oneHour
                      + 0.1 * Long(usage, "cache_read_input_tokens") + 5 * Long(usage, "output_tokens"));
    }

    /// <returns>Whether the line carries text or an image, not only tool results.</returns>
    private static bool ParseUser(JsonElement message, DateTimeOffset at, List<AgentObservation> observations,
        List<string> toolResultIds)
    {
        // A plain prompt starts work. A line of only tool results changes nothing: the tool_use
        // before it already said "working", and after a hand-back the result must not undo "done".
        var prompt = false;
        var interrupted = false;

        if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
        {
            prompt = true;
            interrupted = content.GetString()!.StartsWith("[Request interrupted", StringComparison.Ordinal);
        }

        foreach (var block in Blocks(message))
        {
            var type = Str(block, "type");
            if (type == "tool_result" && Str(block, "tool_use_id") is { } resultFor)
                toolResultIds.Add(resultFor);
            if (type == "tool_result"
                && block.TryGetProperty("is_error", out var isError) && isError.ValueKind == JsonValueKind.True)
                observations.Add(new ToolErrorObserved(at));

            if (type is "text" or "image")
                prompt = true;

            if (type == "text" && Str(block, "text") is { } text
                && text.StartsWith("[Request interrupted", StringComparison.Ordinal))
                interrupted = true;
        }

        if (prompt)
            observations.Add(new StatusObserved(at, interrupted ? AgentStatus.Idle : AgentStatus.Working));
        return prompt;
    }

    [SuppressMessage("Performance", "CA1859", Justification = "Non-array content yields an empty sequence, which ArrayEnumerator cannot express.")]
    private static IEnumerable<JsonElement> Blocks(JsonElement message) =>
        message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
            ? content.EnumerateArray()
            : [];

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static long Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
}
