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
public sealed class DemoSource(IAgentSink sink, IUsageSink usage, IOptions<DemoOptions> options, TimeProvider clock) : BackgroundService
{
    // A compressed 5-hour window, so the treasure chest visibly drains and refills.
    private static readonly TimeSpan DemoWindow = TimeSpan.FromMinutes(3);

    // What a real session shows: its title, then host · project · branch. Short enough to fit above a fish.
    private static readonly (string Title, string Detail, string Source)[] Sessions =
    [
        ("Invoice PDF rounding fix", "Claude Code · billing-api · fix/rounding", "claude-code"),
        ("Checkout coupon codes", "T3 Code · Codex · web-shop · coupons", "codex"),
        ("Postgres failover runbook", "T3 Code · Claude Code · infra · main", "claude-code"),
        ("Offline sync for orders", "Codex · mobile-app · offline-sync", "codex"),
        ("Search for API reference", "T3 Code · Claude Code · docs-site · search", "claude-code"),
    ];

    private static readonly string[] Tools = ["Read", "Edit", "Bash", "Grep", "Glob", "WebFetch", "Write"];
    private static readonly string[] SubagentTypes = ["Explore", "general-purpose", "Plan", "code-reviewer"];
    private static readonly string[] SubagentTasks =
        ["Find rounding call sites", "Review the diff", "Plan the migration", "Map the sync API", "Check test coverage"];
    private static readonly string[] WaitReasons = ["permission prompt", "a question", "plan approval"];

    private readonly Random _random = new();
    private readonly List<Fake> _agents = [];
    private int _next;
    private readonly DemoLimit[] _limits = [new("claude-code"), new("codex")];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
            return;

        for (var i = 0; i < options.Value.Sessions; i++)
            Spawn(null);
        foreach (var limit in _limits)
        {
            limit.Used = 10 + _random.NextDouble() * 15;
            limit.Resets = clock.GetUtcNow() + DemoWindow;
        }
        // Give the demo an immediately readable school and a question to attend to.
        foreach (var parent in _agents.Take(2).ToList())
            Spawn(parent);
        if (_agents.FirstOrDefault(a => a.Depth == 0 && a.Source == "codex") is { } waiting)
        {
            Spawn(waiting);
            waiting.Working = false;
            waiting.StateLockedUntil = clock.GetUtcNow() + TimeSpan.FromMinutes(1);
            sink.Observe(waiting.Id, new StatusObserved(clock.GetUtcNow(), AgentStatus.Waiting, "a question"));
        }

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
                    // Roughly what a response over this much context costs: cached reads plus a little output.
                    sink.Observe(agent.Id, new SpendObserved(now, null, agent.Tokens / 10 + _random.Next(2_000, 8_000)));
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

            // Mains alternate between working and waiting on the user, finished or blocked mid-turn;
            // subagents work, then finish.
            if (agent.Depth == 0 && now >= agent.StateLockedUntil && _random.NextDouble() < 0.01)
            {
                agent.Working = !agent.Working;
                sink.Observe(agent.Id, agent.Working
                    ? new StatusObserved(now, AgentStatus.Working)
                    : _random.NextDouble() < 0.4
                        ? new StatusObserved(now, AgentStatus.Waiting, WaitReasons[_random.Next(WaitReasons.Length)])
                        : new StatusObserved(now, AgentStatus.Idle));
            }
            else if (agent.Depth > 0 && _random.NextDouble() < 0.008)
            {
                agent.Done = true;
                sink.Observe(agent.Id, new StatusObserved(now, AgentStatus.Done));
            }
        }

        _agents.RemoveAll(a => a.Done);
        ReportUsage(now);
    }

    /// <summary>Busy agents spend the window; it refills when it resets.</summary>
    private void ReportUsage(DateTimeOffset now)
    {
        foreach (var limit in _limits)
        {
            if (now >= limit.Resets)
            {
                limit.Used = 0;
                limit.Resets = now + DemoWindow;
            }
            limit.Used = Math.Min(100, limit.Used + _agents.Count(a => a.Working && a.Source == limit.Source) * _random.NextDouble() * 0.15);
            usage.Report(new UsageSnapshot(
                new UsageWindow(Math.Round(limit.Used), limit.Resets),
                new UsageWindow(limit.Source == "codex" ? 18 : 37, now.AddDays(3)),
                now,
                limit.Source));
        }
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
        var (label, detail, source) = parent is null
            ? Sessions[(_next - 1) % Sessions.Length]
            : (SubagentTasks[_random.Next(SubagentTasks.Length)], SubagentTypes[_random.Next(SubagentTypes.Length)], parent.Source);
        agent.Source = source;
        sink.Upsert(new AgentDescriptor(id, parent?.Id, sessionId, source, label, detail, depth));

        var now = clock.GetUtcNow();
        sink.Observe(id, new ContextObserved(now, agent.Tokens));
        sink.Observe(id, new StatusObserved(now, AgentStatus.Working));
    }

    private sealed class DemoLimit(string source)
    {
        public string Source { get; } = source;
        public double Used { get; set; }
        public DateTimeOffset Resets { get; set; }
    }

    private sealed class Fake(string id, string? parentId, int depth)
    {
        public string Id { get; } = id;
        public string? ParentId { get; } = parentId;
        public int Depth { get; } = depth;
        public string SessionId { get; set; } = id;
        public string Source { get; set; } = "claude-code";
        public long Tokens { get; set; }
        public double Busyness { get; init; }
        public bool Working { get; set; } = true;
        public DateTimeOffset StateLockedUntil { get; set; }
        public bool Done { get; set; }
    }
}
