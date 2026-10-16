namespace AgentAquarium.Domain;

// The source-neutral model. Every agent source (Claude Code today, others later)
// translates its own format into these types; nothing past this seam knows where
// an agent came from except the Source label.

public enum AgentStatus
{
    /// <summary>Model is generating or a tool is running.</summary>
    Working,
    /// <summary>Turn ended; waiting for the user.</summary>
    Idle,
    /// <summary>Blocked mid-turn on the user: a permission prompt, a question, a plan to approve.</summary>
    Waiting,
    /// <summary>Subagent finished its task; it will leave the tank.</summary>
    Done,
    /// <summary>
    /// Turn ended, but work it started in the background is still running and will wake it when it
    /// finishes: not the user's turn yet.
    /// </summary>
    Background,
}

/// <summary>Identity and lineage of an agent. Upserted whenever a source learns more about it.</summary>
/// <param name="Id">Unique across all sources; a source whose native ids could clash with another's must prefix them.</param>
/// <param name="LifetimeManaged">
/// The source knows when this agent really ends and will call <see cref="IAgentSink.Remove"/>;
/// the tank must not forget it just because it has been quiet.
/// </param>
public sealed record AgentDescriptor(
    string Id,
    string? ParentId,
    string SessionId,
    string Source,
    string Label,
    string? Detail = null,
    int Depth = 0,
    bool LifetimeManaged = false);

public abstract record AgentObservation(DateTimeOffset At);

/// <summary>Current context size, in tokens.</summary>
public sealed record ContextObserved(DateTimeOffset At, long Tokens) : AgentObservation(At);

/// <summary>
/// Tokens one model response cost, weighted the way API pricing weighs them, so a cache read
/// counts for less than fresh input or output. Not an exact cost, but what the limit tracks.
/// </summary>
/// <param name="MessageId">A response spread over several transcript lines repeats its usage; only the latest per id counts.</param>
public sealed record SpendObserved(DateTimeOffset At, string? MessageId, long Tokens) : AgentObservation(At);

public sealed record ToolCallObserved(DateTimeOffset At, string ToolName) : AgentObservation(At);

public sealed record ToolErrorObserved(DateTimeOffset At) : AgentObservation(At);

public sealed record CompactionObserved(DateTimeOffset At, long PreTokens, long PostTokens) : AgentObservation(At);

/// <param name="WaitingFor">For <see cref="AgentStatus.Waiting"/>: what the agent needs from the user, when known.</param>
public sealed record StatusObserved(DateTimeOffset At, AgentStatus Status, string? WaitingFor = null) : AgentObservation(At);

/// <summary>Where sources report to. Implemented by the tank's registry.</summary>
public interface IAgentSink
{
    void Upsert(AgentDescriptor descriptor);
    /// <summary>Ignored for an agent that has not been upserted yet: describe first, then observe.</summary>
    void Observe(string agentId, AgentObservation observation);
    /// <summary>Removes the agent and all its descendants.</summary>
    void Remove(string agentId);
}

/// <summary>What the browser receives, a few times per second.</summary>
/// <param name="SpentTokens">Weighted tokens spent while the tank watched, history left out; only the growth means anything.</param>
/// <param name="StatusSince">When the agent entered its current status; how long it has been waiting, for Idle and Waiting.</param>
public sealed record AgentSnapshot(
    string Id,
    string? ParentId,
    string SessionId,
    string Source,
    string Label,
    string? Detail,
    int Depth,
    long ContextTokens,
    long SpentTokens,
    AgentStatus Status,
    string? WaitingFor,
    DateTimeOffset StatusSince,
    double Activity,
    int ToolCalls,
    int Errors,
    int Compactions,
    string? LastTool,
    DateTimeOffset LastSeen);
