using AgentAquarium.Domain;
using Microsoft.Extensions.Options;

namespace AgentAquarium.Tank;

public sealed class TankOptions
{
    /// <summary>A Working agent that has been silent this long is shown as Idle.</summary>
    public TimeSpan StallAfter { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>An agent silent this long leaves the tank.</summary>
    public TimeSpan ForgetAfter { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>A Done subagent leaves the tank after this long.</summary>
    public TimeSpan DoneLinger { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>Window over which recent events count towards activity.</summary>
    public TimeSpan ActivityWindow { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>Thread-safe store of everything in the tank. Sources write, the stream reads.</summary>
public sealed class AgentRegistry(IOptions<TankOptions> options, TimeProvider clock) : IAgentSink
{
    private readonly TankOptions _options = options.Value;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, AgentState> _agents = [];

    public void Upsert(AgentDescriptor descriptor)
    {
        lock (_gate)
        {
            if (_agents.TryGetValue(descriptor.Id, out var state))
                state.Descriptor = descriptor;
            else
                _agents[descriptor.Id] = new AgentState(descriptor, clock.GetUtcNow());
        }
    }

    public void Observe(string agentId, AgentObservation observation)
    {
        lock (_gate)
        {
            if (!_agents.TryGetValue(agentId, out var state))
                return;

            // Until the first observation LastSeen is just "when we found it"; replaying an
            // old transcript must be able to move it back in time so stale agents get pruned.
            state.LastSeen = state.Observed ? Max(state.LastSeen, observation.At) : observation.At;
            state.Observed = true;

            switch (observation)
            {
                case ContextObserved c:
                    state.ContextTokens = c.Tokens;
                    break;
                case ToolCallObserved t:
                    state.ToolCalls++;
                    state.LastTool = t.ToolName;
                    state.RecentEvents.Enqueue(t.At);
                    break;
                case ToolErrorObserved e:
                    state.Errors++;
                    state.RecentEvents.Enqueue(e.At);
                    break;
                case CompactionObserved c:
                    state.Compactions++;
                    state.ContextTokens = c.PostTokens;
                    break;
                case StatusObserved s:
                    state.Status = s.Status;
                    if (s.Status == AgentStatus.Working)
                        state.RecentEvents.Enqueue(s.At);
                    break;
            }
        }
    }

    public void Remove(string agentId)
    {
        lock (_gate)
            RemoveWithDescendants([agentId]);
    }

    public IReadOnlyList<AgentSnapshot> Snapshot()
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            Prune(now);
            return _agents.Values.Select(s => s.ToSnapshot(now, _options)).ToList();
        }
    }

    private void Prune(DateTimeOffset now)
    {
        var gone = _agents.Values
            .Where(s => (!s.Descriptor.LifetimeManaged && now - s.LastSeen > _options.ForgetAfter)
                        || (s.Status == AgentStatus.Done && now - s.LastSeen > _options.DoneLinger))
            .Select(s => s.Descriptor.Id)
            .ToList();

        RemoveWithDescendants(gone);
    }

    /// <summary>Children follow their parent out of the tank.</summary>
    private void RemoveWithDescendants(List<string> ids)
    {
        var pending = new Queue<string>(ids);
        while (pending.TryDequeue(out var id))
        {
            _agents.Remove(id);
            foreach (var child in _agents.Values.Where(s => s.Descriptor.ParentId == id))
                pending.Enqueue(child.Descriptor.Id);
        }
    }

    private static DateTimeOffset Max(DateTimeOffset a, DateTimeOffset b) => a > b ? a : b;

    private sealed class AgentState(AgentDescriptor descriptor, DateTimeOffset firstSeen)
    {
        public AgentDescriptor Descriptor { get; set; } = descriptor;
        public DateTimeOffset LastSeen { get; set; } = firstSeen;
        public bool Observed { get; set; }
        public AgentStatus Status { get; set; } = AgentStatus.Idle;
        public long ContextTokens { get; set; }
        public int ToolCalls { get; set; }
        public int Errors { get; set; }
        public int Compactions { get; set; }
        public string? LastTool { get; set; }
        public Queue<DateTimeOffset> RecentEvents { get; } = new();

        public AgentSnapshot ToSnapshot(DateTimeOffset now, TankOptions options)
        {
            while (RecentEvents.TryPeek(out var t) && now - t > options.ActivityWindow)
                RecentEvents.Dequeue();

            var status = Status == AgentStatus.Working && now - LastSeen > options.StallAfter
                ? AgentStatus.Idle
                : Status;

            // 0..1: a busy agent does roughly one tool call every few seconds.
            var activity = status == AgentStatus.Working
                ? Math.Clamp(0.35 + RecentEvents.Count / 20.0, 0, 1)
                : 0;

            var d = Descriptor;
            return new AgentSnapshot(d.Id, d.ParentId, d.SessionId, d.Source, d.Label, d.Detail, d.Depth,
                ContextTokens, status, activity, ToolCalls, Errors, Compactions, LastTool, LastSeen);
        }
    }
}
