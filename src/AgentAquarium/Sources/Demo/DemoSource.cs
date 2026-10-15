using AgentAquarium.Domain;
using Microsoft.Extensions.Options;

namespace AgentAquarium.Sources.Demo;

public sealed class DemoOptions
{
    public bool Enabled { get; set; }
    public int Sessions { get; set; } = 3;
}

/// <summary>
/// Synthetic agents, for working on the tank without real sessions running.
/// Also the smallest example of what a new source has to do: upsert, then observe.
/// </summary>
public sealed class DemoSource(IAgentSink sink, IOptions<DemoOptions> options, TimeProvider clock) : BackgroundService
{
    private static readonly string[] Projects = ["billing-api", "web-shop", "infra", "mobile-app", "docs-site"];
    private static readonly string[] Tools = ["Read", "Edit", "Bash", "Grep", "Glob", "WebFetch", "Write"];
    private static readonly string[] SubagentTypes = ["Explore", "general-purpose", "Plan", "code-reviewer"];

    private readonly Random _random = new();
    private readonly List<Fake> _agents = [];
    private int _next;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
            return;

        for (var i = 0; i < options.Value.Sessions; i++)
            Spawn(null);

        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(500), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            Tick();
    }

    private void Tick()
    {
        var now = clock.GetUtcNow();
        foreach (var agent in _agents.ToList())
        {
            if (agent.Done)
                continue;

            if (agent.Working)
            {
                if (_random.NextDouble() < agent.Busyness)
                {
                    sink.Observe(agent.Id, new ToolCallObserved(now, Tools[_random.Next(Tools.Length)]));
                    agent.Tokens += _random.Next(800, 6000);
                    sink.Observe(agent.Id, new ContextObserved(now, agent.Tokens));
                }
                if (_random.NextDouble() < 0.01)
                    sink.Observe(agent.Id, new ToolErrorObserved(now));
                if (agent.Tokens > 180_000 && _random.NextDouble() < 0.05)
                {
                    sink.Observe(agent.Id, new CompactionObserved(now, agent.Tokens, 15_000));
                    agent.Tokens = 15_000;
                }
                if (agent.Depth < 2 && _random.NextDouble() < 0.012 && Children(agent) < 4)
                    Spawn(agent);
            }

            // Mains alternate between working and waiting; subagents work, then finish.
            if (agent.Depth == 0 && _random.NextDouble() < 0.01)
            {
                agent.Working = !agent.Working;
                sink.Observe(agent.Id, new StatusObserved(now, agent.Working ? AgentStatus.Working : AgentStatus.Idle));
            }
            else if (agent.Depth > 0 && _random.NextDouble() < 0.008)
            {
                agent.Done = true;
                sink.Observe(agent.Id, new StatusObserved(now, AgentStatus.Done));
            }
        }

        _agents.RemoveAll(a => a.Done);
    }

    private int Children(Fake parent) => _agents.Count(a => a.ParentId == parent.Id);

    private void Spawn(Fake? parent)
    {
        var id = $"demo-{++_next}";
        var depth = parent is null ? 0 : parent.Depth + 1;
        var agent = new Fake(id, parent?.Id, depth)
        {
            Tokens = _random.Next(8_000, 40_000),
            Busyness = 0.2 + _random.NextDouble() * 0.6,
        };
        _agents.Add(agent);

        var sessionId = parent?.SessionId ?? id;
        agent.SessionId = sessionId;
        var label = parent is null ? Projects[(_next - 1) % Projects.Length] : SubagentTypes[_random.Next(SubagentTypes.Length)];
        sink.Upsert(new AgentDescriptor(id, parent?.Id, sessionId, "demo", label,
            parent is null ? "demo session" : "demo subagent task", depth));

        var now = clock.GetUtcNow();
        sink.Observe(id, new ContextObserved(now, agent.Tokens));
        sink.Observe(id, new StatusObserved(now, AgentStatus.Working));
    }

    private sealed class Fake(string id, string? parentId, int depth)
    {
        public string Id { get; } = id;
        public string? ParentId { get; } = parentId;
        public int Depth { get; } = depth;
        public string SessionId { get; set; } = id;
        public long Tokens { get; set; }
        public double Busyness { get; init; }
        public bool Working { get; set; } = true;
        public bool Done { get; set; }
    }
}
