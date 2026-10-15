using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace AgentAquarium.Sources.ClaudeCode;

/// <summary>
/// What Claude Code's <c>PermissionRequest</c> hook posts (see <c>plugin/hooks/hooks.json</c>), as far as we read it.
/// It names the session and the tool, not the tool call: <see cref="ClaudeCodeSource"/> matches it to the call
/// still open in the transcript. <c>agent_id</c> is set when the request comes from a subagent.
/// </summary>
public sealed record PermissionRequestHook(
    [property: JsonPropertyName("session_id")] string? SessionId,
    [property: JsonPropertyName("tool_name")] string? ToolName,
    [property: JsonPropertyName("tool_input")] JsonElement? ToolInput,
    [property: JsonPropertyName("agent_id")] string? AgentId);

/// <summary>A permission prompt the user has not answered yet, as reported by the hook.</summary>
public sealed record PermissionRequest(string SessionId, string ToolName, JsonElement? Input, string? AgentId, DateTimeOffset At);

/// <summary>
/// Hands permission prompts from the hook endpoint to <see cref="ClaudeCodeSource"/>. Bounded, dropping the
/// oldest, because nothing drains it while that source is off.
/// </summary>
public sealed class PermissionRequests
{
    private readonly Channel<PermissionRequest> _channel = Channel.CreateBounded<PermissionRequest>(
        new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest });

    public void Add(PermissionRequest request) => _channel.Writer.TryWrite(request);

    public IEnumerable<PermissionRequest> Drain()
    {
        while (_channel.Reader.TryRead(out var request))
            yield return request;
    }
}
