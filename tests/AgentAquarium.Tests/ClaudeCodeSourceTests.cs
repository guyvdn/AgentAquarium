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
