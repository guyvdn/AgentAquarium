using System.Text.Json;
using AgentAquarium.Domain;

namespace AgentAquarium.Sources.Codex;

/// <summary>Only consumes known rollout records; text, reasoning and tool arguments stay local.</summary>
internal sealed class CodexTranscript
{
    public string? Id { get; private set; }
    public string? ParentId { get; private set; }
    public string? Cwd { get; private set; }
    public string? Branch { get; private set; }
    public string? Originator { get; private set; }
    public string? Title { get; private set; }
    public bool Internal { get; private set; }
    public AgentStatus Status { get; private set; } = AgentStatus.Idle;
    public Dictionary<string, string> OpenCalls { get; } = new(StringComparer.Ordinal);
    private readonly HashSet<string> _waits = new(StringComparer.Ordinal);
    private long _context;
    private long? _weightedTotal;
    public UsageSnapshot? Usage { get; private set; }

    public IReadOnlyList<AgentObservation> Read(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return Read(doc.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            return [];
        }
    }

    private List<AgentObservation> Read(JsonElement root)
    {
        var observations = new List<AgentObservation>();
        if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            return observations;
        var type = Text(root, "type");
        if (type == "session_meta")
        {
            ReadMetadata(payload);
            return observations;
        }
        if (!DateTimeOffset.TryParse(Text(root, "timestamp"), System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var at))
            return observations;
        var kind = Text(payload, "type");
        if (type == "event_msg")
            ReadEvent(payload, kind, at, observations);
        else if (type == "compacted")
        {
            observations.Add(new CompactionObserved(at, _context, 0));
            _context = 0;
        }
        else if (type == "response_item")
            ReadResponse(payload, kind, at, observations);
        return observations;
    }

    private void ReadEvent(JsonElement payload, string? kind, DateTimeOffset at, List<AgentObservation> observations)
    {
        switch (kind)
        {
            case "user_message":
                _waits.Clear();
                SetStatus(AgentStatus.Working, at, observations);
                break;
            case "task_started":
                SetStatus(AgentStatus.Working, at, observations);
                break;
            case "task_complete":
            case "turn_aborted":
                OpenCalls.Clear();
                _waits.Clear();
                SetStatus(ParentId is null ? AgentStatus.Idle : AgentStatus.Done, at, observations);
                break;
            case "token_count":
                ReadTokens(payload, at, observations);
                break;
            case "context_compacted":
                observations.Add(new CompactionObserved(at, _context, 0));
                _context = 0;
                break;
        }
    }

    private void ReadTokens(JsonElement payload, DateTimeOffset at, List<AgentObservation> observations)
    {
        Usage = CodexUsage.Parse(payload, at) ?? Usage;
        if (!payload.TryGetProperty("info", out var info) || info.ValueKind != JsonValueKind.Object)
            return;
        if (info.TryGetProperty("last_token_usage", out var last))
        {
            _context = Number(last, "total_tokens");
            observations.Add(new ContextObserved(at, _context));
        }
        if (info.TryGetProperty("total_token_usage", out var total))
        {
            var input = Number(total, "input_tokens");
            var cached = Math.Clamp(Number(total, "cached_input_tokens"), 0, input);
            var weighted = input - cached + cached / 10 + Number(total, "output_tokens") * 5;
            if (_weightedTotal is { } previous && weighted > previous)
                observations.Add(new SpendObserved(at, null, weighted - previous));
            _weightedTotal = weighted;
        }
    }

    private void ReadResponse(JsonElement payload, string? kind, DateTimeOffset at, List<AgentObservation> observations)
    {
        var callId = Text(payload, "call_id");
        if (kind is "function_call" or "custom_tool_call" && callId is not null)
        {
            var tool = Text(payload, "name") ?? "tool";
            if (OpenCalls.TryAdd(callId, tool))
                observations.Add(new ToolCallObserved(at, tool));
            if (tool.EndsWith("request_user_input", StringComparison.Ordinal)
                || tool.EndsWith("request_user_input_async", StringComparison.Ordinal))
                _waits.Add(callId);
            SetStatus(_waits.Count > 0 ? AgentStatus.Waiting : AgentStatus.Working, at, observations);
        }
        else if (kind is "function_call_output" or "custom_tool_call_output" && callId is not null)
        {
            OpenCalls.Remove(callId);
            _waits.Remove(callId);
            if (payload.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Object
                && output.TryGetProperty("isError", out var error) && error.ValueKind == JsonValueKind.True)
                observations.Add(new ToolErrorObserved(at));
            SetStatus(_waits.Count > 0 ? AgentStatus.Waiting : AgentStatus.Working, at, observations);
        }
        else if (kind == "message" && Text(payload, "role") == "assistant" && Text(payload, "phase") == "final_answer")
        {
            OpenCalls.Clear();
            _waits.Clear();
            SetStatus(ParentId is null ? AgentStatus.Idle : AgentStatus.Done, at, observations);
        }
    }

    private void SetStatus(AgentStatus status, DateTimeOffset at, List<AgentObservation> observations)
    {
        Status = status;
        observations.Add(new StatusObserved(at, status, status == AgentStatus.Waiting ? "question" : null));
    }

    private void ReadMetadata(JsonElement payload)
    {
        Id = Text(payload, "id");
        ParentId = Text(payload, "parent_thread_id");
        Cwd = Text(payload, "cwd");
        Originator = Text(payload, "originator");
        if (payload.TryGetProperty("git", out var git))
            Branch = Text(git, "branch");
        if (payload.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.Object
            && source.TryGetProperty("subagent", out var sub) && sub.ValueKind == JsonValueKind.Object)
        {
            Internal = Text(sub, "other") == "guardian";
            if (sub.TryGetProperty("thread_spawn", out var spawn))
            {
                ParentId ??= Text(spawn, "parent_thread_id");
                Title = Text(spawn, "agent_nickname");
            }
        }
    }

    private static string? Text(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
        && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static long Number(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property)
        && property.TryGetInt64(out var number) ? number : 0;
}
