using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using AgentAquarium.Domain;

namespace AgentAquarium.Sources.ClaudeCode;

/// <summary>What one transcript line tells us, already in source-neutral terms.</summary>
public sealed record TranscriptLine(
    IReadOnlyList<AgentObservation> Observations,
    IReadOnlyList<string> ToolUseIds,
    string? Cwd = null,
    string? GitBranch = null,
    string? Title = null,
    string? Entrypoint = null)
{
    public static readonly TranscriptLine Empty = new([], []);
}

/// <summary>
/// Parses one line of a Claude Code session transcript (<c>~/.claude/projects/&lt;slug&gt;/*.jsonl</c>).
/// The format is undocumented and evolves, so everything is read defensively: unknown
/// record types and missing fields are ignored rather than failing the line.
/// </summary>
public static class TranscriptParser
{
    /// <summary>A background subagent ends by calling this tool, not with an end_turn.</summary>
    private const string HandbackTool = "SubagentHandback";

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
        var toolUseIds = new List<string>();

        switch (type)
        {
            case "assistant" when root.TryGetProperty("message", out var message):
                ParseAssistant(message, at, observations, toolUseIds);
                break;
            case "user" when root.TryGetProperty("message", out var message):
                ParseUser(message, at, observations);
                break;
            case "system" when Str(root, "subtype") == "compact_boundary":
                var meta = root.TryGetProperty("compactMetadata", out var m) ? m : default;
                observations.Add(new CompactionObserved(at, Long(meta, "preTokens"), Long(meta, "postTokens")));
                break;
        }

        return new TranscriptLine(observations, toolUseIds, Str(root, "cwd"), Str(root, "gitBranch"),
            Entrypoint: Str(root, "entrypoint"));
    }

    private static void ParseAssistant(JsonElement message, DateTimeOffset at,
        List<AgentObservation> observations, List<string> toolUseIds)
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
            if (Str(block, "id") is { } id)
                toolUseIds.Add(id);
        }

        // null stop_reason = a partial (streamed) record; the turn is still going.
        var stop = Str(message, "stop_reason");
        var working = !handedBack && stop is null or "tool_use" or "pause_turn";
        observations.Add(new StatusObserved(at, working ? AgentStatus.Working : AgentStatus.Idle));
    }

    private static void ParseUser(JsonElement message, DateTimeOffset at, List<AgentObservation> observations)
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
