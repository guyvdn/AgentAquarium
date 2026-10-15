using AgentAquarium.Domain;
using AgentAquarium.Tank;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AgentAquarium.Tests;

public class AgentRegistryTests
{
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 15, 8, 0, 0, TimeSpan.Zero));
    private readonly AgentRegistry _registry;

    public AgentRegistryTests() => _registry = new AgentRegistry(Options.Create(new TankOptions()), _clock);

    private DateTimeOffset Now => _clock.GetUtcNow();

    [Fact]
    public void Observations_for_unknown_agents_are_ignored()
    {
        _registry.Observe("ghost", new ContextObserved(Now, 10));

        Assert.Empty(_registry.Snapshot());
    }

    [Fact]
    public void Replayed_history_is_dated_by_its_observations_not_by_discovery()
    {
        _registry.Upsert(new AgentDescriptor("a", null, "a", "test", "old session"));
        _registry.Observe("a", new StatusObserved(Now - TimeSpan.FromHours(2), AgentStatus.Working));

        Assert.Empty(_registry.Snapshot());
    }

    [Fact]
    public void Silent_working_agent_is_shown_idle()
    {
        _registry.Upsert(new AgentDescriptor("a", null, "a", "test", "s"));
        _registry.Observe("a", new StatusObserved(Now, AgentStatus.Working));
        _clock.Advance(TimeSpan.FromMinutes(6));

        var agent = Assert.Single(_registry.Snapshot());
        Assert.Equal(AgentStatus.Idle, agent.Status);
        Assert.Equal(0, agent.Activity);
    }

    [Fact]
    public void Done_subagent_lingers_then_leaves()
    {
        _registry.Upsert(new AgentDescriptor("a", null, "a", "test", "s"));
        _registry.Upsert(new AgentDescriptor("a/b", "a", "a", "test", "sub", Depth: 1));
        _registry.Observe("a", new StatusObserved(Now, AgentStatus.Idle));
        _registry.Observe("a/b", new StatusObserved(Now, AgentStatus.Done));

        Assert.Equal(2, _registry.Snapshot().Count);
        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("a", Assert.Single(_registry.Snapshot()).Id);
    }

    [Fact]
    public void Children_leave_with_their_parent()
    {
        _registry.Upsert(new AgentDescriptor("a", null, "a", "test", "s"));
        _registry.Upsert(new AgentDescriptor("a/b", "a", "a", "test", "sub", Depth: 1));
        _registry.Observe("a", new StatusObserved(Now - TimeSpan.FromHours(1), AgentStatus.Idle));
        _registry.Observe("a/b", new StatusObserved(Now, AgentStatus.Working));

        Assert.Empty(_registry.Snapshot());
    }

    [Fact]
    public void Busy_agent_is_more_active_and_compaction_shrinks_context()
    {
        _registry.Upsert(new AgentDescriptor("a", null, "a", "test", "s"));
        _registry.Observe("a", new StatusObserved(Now, AgentStatus.Working));
        var calm = Assert.Single(_registry.Snapshot()).Activity;

        for (var i = 0; i < 10; i++)
            _registry.Observe("a", new ToolCallObserved(Now, "Bash"));
        _registry.Observe("a", new ContextObserved(Now, 250_000));
        _registry.Observe("a", new CompactionObserved(Now, 250_000, 12_000));

        var agent = Assert.Single(_registry.Snapshot());
        Assert.True(agent.Activity > calm);
        Assert.Equal(12_000, agent.ContextTokens);
        Assert.Equal((10, 1), (agent.ToolCalls, agent.Compactions));
    }

    [Fact]
    public void Lifetime_managed_agent_is_not_forgotten_when_quiet()
    {
        _registry.Upsert(new AgentDescriptor("a", null, "a", "test", "s", LifetimeManaged: true));
        _registry.Observe("a", new StatusObserved(Now - TimeSpan.FromHours(3), AgentStatus.Idle));

        Assert.Single(_registry.Snapshot());
    }

    [Fact]
    public void Remove_takes_the_whole_school()
    {
        _registry.Upsert(new AgentDescriptor("a", null, "a", "test", "s"));
        _registry.Upsert(new AgentDescriptor("a/b", "a", "a", "test", "sub", Depth: 1));
        _registry.Upsert(new AgentDescriptor("a/c", "a/b", "a", "test", "subsub", Depth: 2));
        _registry.Upsert(new AgentDescriptor("x", null, "x", "test", "other"));
        foreach (var id in new[] { "a", "a/b", "a/c", "x" })
            _registry.Observe(id, new StatusObserved(Now, AgentStatus.Working));

        _registry.Remove("a");

        Assert.Equal("x", Assert.Single(_registry.Snapshot()).Id);
    }
}
