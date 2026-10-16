using AgentAquarium.Domain;
using AgentAquarium.Sources.ClaudeCode;
using AgentAquarium.Tank;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AgentAquarium.Tests;

public sealed class ClaudeCodeSourceTests : IDisposable
{
    private const string Session = "11111111-2222-3333-4444-555555555555";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"aquarium-{Guid.NewGuid():N}", "projects");
    // Real time: without a live-session registry, transcripts are picked up by their real write time.
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly AgentRegistry _registry;
    private readonly ClaudeCodeSource _source;

    public ClaudeCodeSourceTests()
    {
        _registry = new AgentRegistry(Options.Create(new TankOptions()), _clock);
        _source = new ClaudeCodeSource(_registry, Options.Create(new ClaudeCodeOptions { T3StateFile = "" }), new PermissionRequests(),
            _clock, NullLogger<ClaudeCodeSource>.Instance);
    }

    public void Dispose()
    {
        _source.Dispose();
        Directory.Delete(Path.GetDirectoryName(_root)!, recursive: true);
    }

    private string Stamp => (_clock.GetUtcNow() - TimeSpan.FromMinutes(1)).ToString("O");

    [Fact]
    public void Turn_ended_with_background_work_running_is_not_the_users_turn()
    {
        var project = Directory.CreateDirectory(Path.Combine(_root, "project")).FullName;
        var main = Path.Combine(project, $"{Session}.jsonl");
        File.WriteAllText(main, $$$"""
            {"type":"user","timestamp":"{{{Stamp}}}","toolUseResult":{"backgroundTaskId":"bg1"},"message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","content":"running"}]}}
            {"type":"assistant","timestamp":"{{{Stamp}}}","message":{"stop_reason":"end_turn","content":[{"type":"text","text":"Waiting for it."}]}}

            """);

        _source.Poll([_root]);
        Assert.Equal(AgentStatus.Background, _registry.Snapshot().Single(a => a.Id == Session).Status);

        // The notification wakes the session, which finishes its turn: now it is the user's.
        File.AppendAllText(main, $$$"""
            {"type":"user","timestamp":"{{{Stamp}}}","message":{"role":"user","content":"<task-notification>\n<task-id>bg1</task-id>\n<status>completed</status>\n</task-notification>"}}
            {"type":"assistant","timestamp":"{{{Stamp}}}","message":{"stop_reason":"end_turn","content":[{"type":"text","text":"Done."}]}}

            """);
        _source.Poll([_root]);
        Assert.Equal(AgentStatus.Idle, _registry.Snapshot().Single(a => a.Id == Session).Status);
    }

    [Fact]
    public void Monitor_that_expires_without_word_hands_the_turn_back()
    {
        var project = Directory.CreateDirectory(Path.Combine(_root, "project")).FullName;
        File.WriteAllText(Path.Combine(project, $"{Session}.jsonl"), $$$"""
            {"type":"user","timestamp":"{{{Stamp}}}","toolUseResult":{"taskId":"m1","timeoutMs":300000},"message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_1","content":"armed"}]}}
            {"type":"assistant","timestamp":"{{{Stamp}}}","message":{"stop_reason":"end_turn","content":[{"type":"text","text":"Watching."}]}}

            """);

        _source.Poll([_root]);
        Assert.Equal(AgentStatus.Background, _registry.Snapshot().Single(a => a.Id == Session).Status);

        // Its 5 minutes are up; it may have said nothing at all.
        _clock.Advance(TimeSpan.FromMinutes(5));
        _source.Poll([_root]);
        Assert.Equal(AgentStatus.Idle, _registry.Snapshot().Single(a => a.Id == Session).Status);
    }

    [Fact]
    public void Subagent_is_labelled_with_its_task_and_typed_below()
    {
        var project = Directory.CreateDirectory(Path.Combine(_root, "project")).FullName;
        var subagents = Directory.CreateDirectory(Path.Combine(project, Session, "subagents")).FullName;
        File.WriteAllText(Path.Combine(project, $"{Session}.jsonl"), "");
        File.WriteAllText(Path.Combine(subagents, "agent-a1.meta.json"),
            """{"agentType":"general-purpose","description":"Review deck skill changes","toolUseId":"toolu_spawn","spawnDepth":1}""");
        File.WriteAllText(Path.Combine(subagents, "agent-a1.jsonl"),
            $$$"""{"type":"user","timestamp":"{{{Stamp}}}","message":{"role":"user","content":"go"}}""" + "\n");

        _source.Poll([_root]);

        var agent = _registry.Snapshot().Single(a => a.Id == $"{Session}/a1");
        Assert.Equal("Review deck skill changes", agent.Label);
        Assert.Equal("general-purpose", agent.Detail);
    }

    [Fact]
    public void Finished_subagent_read_before_its_parent_does_not_come_back()
    {
        var project = Directory.CreateDirectory(Path.Combine(_root, "project")).FullName;
        var main = Path.Combine(project, $"{Session}.jsonl");
        var subagents = Directory.CreateDirectory(Path.Combine(project, Session, "subagents")).FullName;
        File.WriteAllText(main, "");
        File.WriteAllText(Path.Combine(subagents, "agent-a1.meta.json"),
            """{"agentType":"general-purpose","description":"Review","toolUseId":"toolu_spawn","spawnDepth":1}""");
        File.WriteAllText(Path.Combine(subagents, "agent-a1.jsonl"),
            $$$"""{"type":"assistant","timestamp":"{{{Stamp}}}","message":{"stop_reason":"tool_use","content":[{"type":"tool_use","id":"toolu_back","name":"SubagentHandback","input":{}}]}}""" + "\n");

        // The subagent is replayed to its end before the call that spawned it has been read, and leaves.
        _source.Poll([_root]);
        Assert.DoesNotContain(_registry.Snapshot(), a => a.Id == $"{Session}/a1");

        File.AppendAllText(main,
            $$$"""{"type":"assistant","timestamp":"{{{Stamp}}}","message":{"stop_reason":"tool_use","content":[{"type":"tool_use","id":"toolu_spawn","name":"Agent","input":{}}]}}""" + "\n");
        _source.Poll([_root]);

        Assert.DoesNotContain(_registry.Snapshot(), a => a.Id == $"{Session}/a1");
    }
}
