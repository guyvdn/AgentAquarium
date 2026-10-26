using System.Diagnostics.CodeAnalysis;
using AgentAquarium.Domain;
using AgentAquarium.Sources.Codex;
using AgentAquarium.Tank;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AgentAquarium.Tests;

public sealed class CodexSourceTests : IDisposable
{
    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"aquarium-codex-{Guid.NewGuid():N}")).FullName;
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);
    private readonly AgentRegistry _registry;
    private readonly CodexSource _source;
    private readonly UsageBoard _usage = new();
    private string Root => Path.Combine(_dir, "sessions");

    public CodexSourceTests()
    {
        Directory.CreateDirectory(Root);
        _registry = new AgentRegistry(Options.Create(new TankOptions()), _clock);
        _source = new CodexSource(_registry, _usage, Options.Create(new CodexOptions { T3StateFile = "" }),
            _clock, NullLogger<CodexSource>.Instance);
    }

    public void Dispose()
    {
        _source.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private string Write(string id, string source = "\"cli\"", string? parent = null)
    {
        var path = Path.Combine(Root, $"rollout-2026-10-26T10-00-00-{id}.jsonl");
        File.WriteAllText(path, $$$$"""
            {"type":"session_meta","payload":{"id":"{{{{id}}}}","cwd":"D:\\repo","source":{{{{source}}}},"parent_thread_id":{{{{(parent is null ? "null" : $"\"{parent}\"")}}}},"git":{"branch":"main"}}}

            """);
        return path;
    }

    private void Append(string path, string type, string payload) =>
        File.AppendAllText(path, $$$$"""{"timestamp":"{{{{_clock.GetUtcNow():O}}}}","type":"{{{{type}}}}","payload":{{{{payload}}}}}""" + "\n");

    [SuppressMessage("Security", "CA2100", Justification = "Test fixture; the SQL is built from the test's own literals.")]
    private static void ExecuteSql(string file, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={file};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    [Fact]
    public void Tools_questions_context_and_turn_completion_follow_the_log()
    {
        var path = Write("main");
        Append(path, "event_msg", """{"type":"task_started"}""");
        Append(path, "response_item", """{"type":"function_call","name":"exec_command","call_id":"tool"}""");
        Append(path, "event_msg", """{"type":"token_count","info":{"last_token_usage":{"input_tokens":800,"cached_input_tokens":600,"output_tokens":100,"total_tokens":900}}}""");
        _source.Poll([Root]);
        var fish = Assert.Single(_registry.Snapshot());
        Assert.Equal("codex/main", fish.Id);
        Assert.Equal("codex", fish.Source);
        Assert.Equal(900, fish.ContextTokens); // cached input is already part of input
        Assert.Equal(1, fish.ToolCalls);
        Assert.Equal(AgentStatus.Working, fish.Status);
        _clock.Advance(TimeSpan.FromMinutes(10));
        _source.Poll([Root]);
        Assert.Equal(AgentStatus.Working, Assert.Single(_registry.Snapshot()).Status);

        Append(path, "response_item", """{"type":"function_call","name":"request_user_input","call_id":"question"}""");
        _source.Poll([Root]);
        Assert.Equal(AgentStatus.Waiting, Assert.Single(_registry.Snapshot()).Status);
        Append(path, "response_item", """{"type":"function_call_output","call_id":"question","output":"answer"}""");
        Append(path, "event_msg", """{"type":"task_complete"}""");
        _source.Poll([Root]);
        Assert.Equal(AgentStatus.Idle, Assert.Single(_registry.Snapshot()).Status);
        Assert.Equal(2, Assert.Single(_registry.Snapshot()).ToolCalls);
        _source.Poll([Root]);
        Assert.Equal(2, Assert.Single(_registry.Snapshot()).ToolCalls);
    }

    [Fact]
    public void Guardians_are_hidden_and_children_finish_without_returning()
    {
        var parent = Write("parent");
        var child = Write("child", """{"subagent":{"thread_spawn":{"parent_thread_id":"parent","agent_nickname":"Scout"}}}""");
        Write("guardian", """{"subagent":{"other":"guardian"}}""", "parent");
        Append(parent, "event_msg", """{"type":"task_started"}""");
        Append(child, "event_msg", """{"type":"task_complete"}""");
        _source.Poll([Root]);
        Assert.Equal(2, _registry.Snapshot().Count);
        var fish = _registry.Snapshot().Single(a => a.Id == "codex/child");
        Assert.Equal("codex/parent", fish.ParentId);
        Assert.Equal("codex/parent", fish.SessionId);
        Assert.Equal("Scout", fish.Label);
        Assert.Equal(AgentStatus.Done, fish.Status);
        _clock.Advance(TimeSpan.FromSeconds(21));
        Assert.Single(_registry.Snapshot());
        _source.Poll([Root]);
        Assert.Single(_registry.Snapshot());
    }

    [Fact]
    public void Partial_lines_and_malformed_records_do_not_lose_the_next_event()
    {
        var path = Write("partial");
        File.AppendAllText(path, "not json\n{\"type\":\"event_msg\",\"payload\":null}\n");
        var line = $$$$"""{"timestamp":"{{{{_clock.GetUtcNow():O}}}}","type":"event_msg","payload":{"type":"task_started"}}""";
        File.AppendAllText(path, line[..20]);
        _source.Poll([Root]);
        Assert.Equal(AgentStatus.Idle, Assert.Single(_registry.Snapshot()).Status);
        File.AppendAllText(path, line[20..] + "\n");
        _source.Poll([Root]);
        Assert.Equal(AgentStatus.Working, Assert.Single(_registry.Snapshot()).Status);
    }

    [Fact]
    public void Old_logs_are_removed_without_a_live_signal()
    {
        var path = Write("old");
        Append(path, "event_msg", """{"type":"task_complete"}""");
        _source.Poll([Root]);
        Assert.Single(_registry.Snapshot());
        _clock.Advance(TimeSpan.FromMinutes(31));
        _source.Poll([Root]);
        Assert.Empty(_registry.Snapshot());
    }

    [Fact]
    public void Usage_reaches_its_own_board_and_only_new_token_growth_spends_coins()
    {
        var path = Write("spending");
        const string initial = """
            {"type":"token_count","info":{"total_token_usage":{"input_tokens":1000,"cached_input_tokens":800,"output_tokens":100}},
             "rate_limits":{"limit_id":"codex","primary":{"used_percent":12,"window_minutes":300}}}
            """;
        // JSONL needs one complete JSON record per line.
        Append(path, "event_msg", initial.ReplaceLineEndings(""));
        _source.Poll([Root]);
        Assert.Equal("codex", Assert.Single(_usage.Snapshot()).Source);
        Assert.Equal(12, Assert.Single(_usage.Snapshot()).FiveHour!.UsedPercent);
        Assert.Equal(0, Assert.Single(_registry.Snapshot()).SpentTokens);
        const string next = """{"type":"token_count","info":{"total_token_usage":{"input_tokens":2000,"cached_input_tokens":1600,"output_tokens":200}}}""";
        Append(path, "event_msg", next);
        _source.Poll([Root]);
        Assert.Equal(780, Assert.Single(_registry.Snapshot()).SpentTokens);
        Append(path, "event_msg", next);
        _source.Poll([Root]);
        Assert.Equal(780, Assert.Single(_registry.Snapshot()).SpentTokens);
    }

    [Fact]
    public void Held_writer_lock_keeps_an_idle_session_until_the_writer_exits()
    {
        // Windows byte-range locks are per handle; Unix fcntl locks are per process.
        if (!OperatingSystem.IsWindows())
            return;
        const string id = "11111111-2222-3333-4444-555555555555";
        var transcript = Write(id);
        Append(transcript, "event_msg", """{"type":"task_complete"}""");
        var locks = Directory.CreateDirectory(Path.Combine(_dir, "thread-writer-locks")).FullName;
        var path = Path.Combine(locks, id + ".lock");
        using var writer = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite);
        writer.Lock(0, 1);
        try
        {
            Assert.True(CodexSource.WriterIsLive(path));
            _source.Poll([Root]);
            Assert.Single(_registry.Snapshot());
            _clock.Advance(TimeSpan.FromHours(1));
            _source.Poll([Root]);
            Assert.Equal(AgentStatus.Idle, Assert.Single(_registry.Snapshot()).Status);
        }
        finally
        {
            writer.Unlock(0, 1);
        }
        _source.Poll([Root]);
        Assert.Empty(_registry.Snapshot());
    }

    [Fact]
    public void Resuming_a_closed_session_restores_context_and_tool_history_without_spending()
    {
        if (!OperatingSystem.IsWindows())
            return;
        const string id = "11111111-2222-3333-4444-555555555555";
        var transcript = Write(id);
        Append(transcript, "response_item", """{"type":"function_call","name":"exec_command","call_id":"tool"}""");
        Append(transcript, "event_msg", """{"type":"token_count","info":{"last_token_usage":{"total_tokens":900},"total_token_usage":{"input_tokens":800,"output_tokens":100}}}""");
        Append(transcript, "event_msg", """{"type":"token_count","info":{"last_token_usage":{"total_tokens":1800},"total_token_usage":{"input_tokens":1600,"output_tokens":200}}}""");
        var locks = Directory.CreateDirectory(Path.Combine(_dir, "thread-writer-locks")).FullName;
        using var writer = new FileStream(Path.Combine(locks, id + ".lock"), FileMode.Create,
            FileAccess.ReadWrite, FileShare.ReadWrite);
        writer.Lock(0, 1);
        try
        {
            _source.Poll([Root]);
            Assert.Equal(1800, Assert.Single(_registry.Snapshot()).ContextTokens);
            Assert.Equal(1, Assert.Single(_registry.Snapshot()).ToolCalls);
        }
        finally
        {
            writer.Unlock(0, 1);
        }
        _source.Poll([Root]);
        Assert.Empty(_registry.Snapshot());
        writer.Lock(0, 1);
        try
        {
            Append(transcript, "event_msg", """{"type":"task_started"}""");
            _source.Poll([Root]);
            var fish = Assert.Single(_registry.Snapshot());
            Assert.Equal(1800, fish.ContextTokens);
            Assert.Equal(1, fish.ToolCalls);
            Assert.Equal(0, fish.SpentTokens);
            Assert.Equal(AgentStatus.Working, fish.Status);
        }
        finally
        {
            writer.Unlock(0, 1);
        }
    }

    /// <summary>A T3 Code state database with one Codex subagent thread, running.</summary>
    private string CreateT3State(string id, string title)
    {
        var state = Path.Combine(_dir, "statev2.sqlite");
        ExecuteSql(state, $$$"""
            create table orchestration_v2_projection_threads (thread_id text primary key, title text not null, updated_at text, archived_at text, deleted_at text, active_provider_thread_id text, payload_json text not null);
            create table orchestration_v2_projection_provider_threads (provider_thread_id text primary key, thread_id text, provider text, driver text, status text, updated_at text, payload_json text);
            create table provider_session_runtime (thread_id text primary key, resume_cursor_json text);
            insert into orchestration_v2_projection_threads values ('t1', '{{{title}}}', '2026-10-26', null, null, 'p1', '{"lineage":{"relationshipToParent":"subagent"}}');
            insert into orchestration_v2_projection_provider_threads values ('p1', 't1', 'codex', 'codex', 'active', '2026-10-26', '{"nativeThreadRef":{"nativeId":"{{{id}}}"}}');
            """);
        return state;
    }

    [Fact]
    public void Finished_t3_thread_leaves_the_tank_although_the_shared_server_holds_its_lock()
    {
        if (!OperatingSystem.IsWindows())
            return;
        const string id = "11111111-2222-3333-4444-555555555555";
        var transcript = Write(id);
        Append(transcript, "event_msg", """{"type":"token_count","info":{"total_token_usage":{"input_tokens":1000}}}""");
        Append(transcript, "event_msg", """{"type":"token_count","info":{"total_token_usage":{"input_tokens":2000}}}""");
        var state = CreateT3State(id, "Review before push");
        void Execute(string sql) => ExecuteSql(state, sql);
        using var source = new CodexSource(_registry, _usage,
            Options.Create(new CodexOptions { T3StateFile = state, T3PollInterval = TimeSpan.FromSeconds(5) }),
            _clock, NullLogger<CodexSource>.Instance);
        var locks = Directory.CreateDirectory(Path.Combine(_dir, "thread-writer-locks")).FullName;
        using var writer = new FileStream(Path.Combine(locks, id + ".lock"), FileMode.Create,
            FileAccess.ReadWrite, FileShare.ReadWrite);
        writer.Lock(0, 1);
        try
        {
            source.Poll([Root]);
            Assert.Equal("Review before push", Assert.Single(_registry.Snapshot()).Label);

            Execute("update orchestration_v2_projection_provider_threads set status = 'idle'");
            _clock.Advance(TimeSpan.FromSeconds(5));
            source.Poll([Root]);
            Assert.Empty(_registry.Snapshot());

            // Resumed: the log is read before T3 Code's state says so, but that turn's spend still counts.
            Append(transcript, "event_msg", """{"type":"token_count","info":{"total_token_usage":{"input_tokens":2500}}}""");
            _clock.Advance(TimeSpan.FromSeconds(1));
            source.Poll([Root]);
            Assert.Empty(_registry.Snapshot());
            Execute("update orchestration_v2_projection_provider_threads set status = 'active'");
            _clock.Advance(TimeSpan.FromSeconds(4));
            source.Poll([Root]);
            Assert.Equal(0, Assert.Single(_registry.Snapshot()).SpentTokens); // history replays first
            source.Poll([Root]);
            Assert.Equal(500, Assert.Single(_registry.Snapshot()).SpentTokens);

            // An idle conversation waits for its user; once the thread moves to another provider, it is done.
            Execute("""
                update orchestration_v2_projection_threads set payload_json = '{}';
                update orchestration_v2_projection_provider_threads set status = 'idle';
                """);
            _clock.Advance(TimeSpan.FromSeconds(5));
            source.Poll([Root]);
            Assert.Single(_registry.Snapshot());
            Execute("update orchestration_v2_projection_threads set active_provider_thread_id = 'p2'");
            _clock.Advance(TimeSpan.FromSeconds(5));
            source.Poll([Root]);
            Assert.Empty(_registry.Snapshot());
        }
        finally
        {
            writer.Unlock(0, 1);
        }
    }

    [Fact]
    public void Released_writer_lock_removes_a_recent_session()
    {
        if (OperatingSystem.IsMacOS())
            return;
        const string id = "11111111-2222-3333-4444-555555555555";
        Write(id);
        var locks = Directory.CreateDirectory(Path.Combine(_dir, "thread-writer-locks")).FullName;
        var path = Path.Combine(locks, id + ".lock");
        File.WriteAllText(path, "");
        Assert.False(CodexSource.WriterIsLive(path));
        _source.Poll([Root]);
        Assert.Empty(_registry.Snapshot());
    }
}
