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
}

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

        switch (type)
        {
            case "assistant" when root.TryGetProperty("message", out var message):
                ParseAssistant(message, at, observations, toolUses, waits);
                break;
            case "user" when root.TryGetProperty("message", out var message):
                // Not text Claude Code adds itself mid-turn (a skill's body, a command caveat), nor text beside a result.
                isPrompt = ParseUser(message, at, observations, toolResultIds) && toolResultIds.Count == 0
                           && !(root.TryGetProperty("isMeta", out var isMeta) && isMeta.ValueKind == JsonValueKind.True);
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
        };
    }

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
