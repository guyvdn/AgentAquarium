using System.Diagnostics.CodeAnalysis;
using AgentAquarium.Sources.ClaudeCode;
using AgentAquarium.Tank;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace AgentAquarium.Tests;

public sealed class T3ThreadTitlesTests : IDisposable
{
    private const string Session = "11111111-2222-3333-4444-555555555555";

    private readonly string _dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"aquarium-{Guid.NewGuid():N}")).FullName;
    private string StateFile => Path.Combine(_dir, "state.sqlite");
    private string Root => Path.Combine(_dir, "projects");

    public T3ThreadTitlesTests() =>
        Execute("""
            create table projection_threads (thread_id text primary key, title text not null, updated_at text, deleted_at text);
            create table provider_session_runtime (thread_id text primary key, resume_cursor_json text);
            """);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [SuppressMessage("Security", "CA2100", Justification = "Test fixture; the SQL is built from the test's own literals.")]
    private void Execute(string sql)
    {
        using var connection = new SqliteConnection($"Data Source={StateFile};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private void AddThread(string threadId, string title, string? resumeCursor, string? deletedAt = null) =>
        Execute($"""
            insert into projection_threads values ('{threadId}', '{title}', '2026-10-16', {(deletedAt is null ? "null" : $"'{deletedAt}'")});
            insert into provider_session_runtime values ('{threadId}', {(resumeCursor is null ? "null" : $"'{resumeCursor}'")});
            """);

    [Fact]
    public void Titles_are_keyed_by_the_resumed_claude_session()
    {
        AddThread("t1", "Thread titles in the aquarium", $$"""{"threadId":"t1","resume":"{{Session}}"}""");
        AddThread("t2", "Deleted thread", """{"resume":"deleted-session"}""", deletedAt: "2026-10-16");
        AddThread("t3", "Not started", null);
        var titles = new T3ThreadTitles(StateFile);

        Assert.True(titles.Refresh());

        Assert.Equal("Thread titles in the aquarium", titles.TitleFor(Session));
        Assert.Null(titles.TitleFor("deleted-session"));
        Assert.False(titles.Refresh()); // nothing changed
    }

    [Fact]
    public void V2_titles_come_from_the_v2_threads_for_imported_and_new_sessions()
    {
        Execute($$$"""
            create table orchestration_v2_projection_threads (thread_id text primary key, title text not null, updated_at text, deleted_at text);
            create table orchestration_v2_projection_provider_threads (provider_thread_id text primary key, thread_id text, provider text, driver text, updated_at text, payload_json text);
            insert into provider_session_runtime values ('old', '{"resume":"imported-session"}');
            insert into projection_threads values ('old', 'Stale legacy title', '2026-10-16', null);
            insert into provider_session_runtime values ('resumed', '{"resume":"{{{Session}}}"}');
            insert into orchestration_v2_projection_threads values ('resumed', 'Same session, older thread', '2026-10-20', null);
            insert into orchestration_v2_projection_threads values ('old', 'Imported thread', '2026-10-20', null);
            insert into orchestration_v2_projection_threads values ('new', 'Thread started after the update', '2026-10-20', null);
            insert into orchestration_v2_projection_threads values ('gone', 'Deleted thread', '2026-10-20', '2026-10-20');
            """);
        Execute($$$"""
            insert into orchestration_v2_projection_provider_threads values
              ('p1', 'new', 'claudeAgent', null, '2026-10-20', '{"nativeThreadRef":{"driver":"claudeAgent","nativeId":"{{{Session}}}"}}'),
              ('p2', 'gone', 'claudeAgent', 'claudeAgent', '2026-10-20', '{"nativeThreadRef":{"nativeId":"deleted-session"}}'),
              ('p3', 'old', 'claudeAgent', 'claudeAgent', '2026-10-20', '{"nativeThreadRef":null}');
            """);
        var titles = new T3ThreadTitles(StateFile);

        Assert.True(titles.Refresh());

        Assert.Equal("Thread started after the update", titles.TitleFor(Session));
        Assert.Equal("Imported thread", titles.TitleFor("imported-session"));
        Assert.Null(titles.TitleFor("deleted-session"));
    }

    [Fact]
    public void Codex_titles_use_the_native_id_and_keep_provider_namespaces_separate()
    {
        Execute("""
            create table orchestration_v2_projection_threads (thread_id text primary key, title text not null, updated_at text, deleted_at text);
            create table orchestration_v2_projection_provider_threads (provider_thread_id text primary key, thread_id text, provider text, driver text, updated_at text, payload_json text);
            insert into orchestration_v2_projection_threads values ('codex-thread', 'Codex task', '2026-10-26', null);
            insert into orchestration_v2_projection_threads values ('claude-thread', 'Claude task', '2026-10-26', null);
            insert into orchestration_v2_projection_provider_threads values
              ('c1', 'codex-thread', 'codex', 'codex', '2026-10-26', '{"nativeThreadRef":{"nativeId":"shared-id"}}'),
              ('c2', 'claude-thread', 'claudeAgent', 'claudeAgent', '2026-10-26', '{"nativeThreadRef":{"nativeId":"shared-id"}}');
            """);
        var codex = new T3ThreadTitles(StateFile, "codex");
        var claude = new T3ThreadTitles(StateFile);
        Assert.True(codex.Refresh());
        Assert.True(claude.Refresh());
        Assert.Equal("Codex task", codex.TitleFor("shared-id"));
        Assert.Equal("Claude task", claude.TitleFor("shared-id"));
    }

    [Fact]
    public void Codex_threads_are_finished_when_deleted_archived_or_idle_and_done()
    {
        Execute("""
            create table orchestration_v2_projection_threads (thread_id text primary key, title text not null, updated_at text, archived_at text, deleted_at text, active_provider_thread_id text, payload_json text not null);
            create table orchestration_v2_projection_provider_threads (provider_thread_id text primary key, thread_id text, provider text, driver text, status text not null, updated_at text, payload_json text);
            insert into orchestration_v2_projection_threads values
              ('open', 'Waiting on the user', '2026-10-26', null, null, 'p-open', '{}'),
              ('settled', 'Settled', '2026-10-26', null, null, 'p-settled', '{"settledOverride":"settled"}'),
              ('settled-busy', 'Settled but running', '2026-10-26', null, null, 'p-settled-busy', '{"settledOverride":"settled"}'),
              ('review', 'Subagent review', '2026-10-26', null, null, 'p-review', '{"lineage":{"relationshipToParent":"subagent"}}'),
              ('archived', 'Archived', '2026-10-26', '2026-10-26', null, 'p-archived', '{}'),
              ('deleted', 'Deleted', '2026-10-26', null, '2026-10-26', 'p-deleted', '{}'),
              ('switched', 'Now on Claude', '2026-10-26', null, null, 'p-claude', '{}'),
              ('resumed', 'Resumed', '2026-10-26', null, null, 'p-resumed-new', '{}');
            insert into orchestration_v2_projection_provider_threads values
              ('p-open', 'open', 'codex', 'codex', 'idle', '2026-10-26', '{"nativeThreadRef":{"nativeId":"open"}}'),
              ('p-settled', 'settled', 'codex', 'codex', 'idle', '2026-10-26', '{"nativeThreadRef":{"nativeId":"settled"}}'),
              ('p-settled-busy', 'settled-busy', 'codex', 'codex', 'active', '2026-10-26', '{"nativeThreadRef":{"nativeId":"settled-busy"}}'),
              ('p-review', 'review', 'codex', 'codex', 'idle', '2026-10-26', '{"nativeThreadRef":{"nativeId":"review"}}'),
              ('p-archived', 'archived', 'codex', 'codex', 'active', '2026-10-26', '{"nativeThreadRef":{"nativeId":"archived"}}'),
              ('p-deleted', 'deleted', 'codex', 'codex', 'active', '2026-10-26', '{"nativeThreadRef":{"nativeId":"deleted"}}'),
              ('p-switched', 'switched', 'codex', 'codex', 'idle', '2026-10-26', '{"nativeThreadRef":{"nativeId":"switched"}}'),
              ('p-claude', 'switched', 'claudeAgent', 'claudeAgent', 'active', '2026-10-26', '{"nativeThreadRef":{"nativeId":"switched-claude"}}'),
              ('p-resumed-new', 'resumed', 'codex', 'codex', 'idle', '2026-10-26', '{"nativeThreadRef":{"nativeId":"resumed"}}'),
              ('p-resumed-stale', 'resumed', 'codex', 'codex', 'idle', '2026-10-27', '{"nativeThreadRef":{"nativeId":"resumed"}}');
            """);
        var codex = new T3ThreadTitles(StateFile, "codex");
        Assert.True(codex.Refresh());

        Assert.False(codex.IsFinished("open"));
        Assert.True(codex.IsFinished("settled"));
        Assert.False(codex.IsFinished("settled-busy"));
        Assert.True(codex.IsFinished("review"));
        Assert.True(codex.IsFinished("archived"));
        Assert.True(codex.IsFinished("deleted"));
        Assert.True(codex.IsFinished("switched"));
        Assert.False(codex.IsFinished("resumed")); // the thread's active provider thread wins over a later one

        // Only the finished set changes: still a change.
        Execute("update orchestration_v2_projection_provider_threads set status = 'active' where provider_thread_id = 'p-review'");
        Assert.True(codex.Refresh());
        Assert.False(codex.IsFinished("review"));

        var claude = new T3ThreadTitles(StateFile);
        Assert.True(claude.Refresh());
        Assert.False(claude.IsFinished("settled"));
    }

    [Fact]
    public void Codex_titles_load_when_the_finished_threads_cannot_be_read()
    {
        // A v2 schema without the columns the finished query needs.
        Execute("""
            create table orchestration_v2_projection_threads (thread_id text primary key, title text not null, updated_at text, deleted_at text);
            create table orchestration_v2_projection_provider_threads (provider_thread_id text primary key, thread_id text, provider text, driver text, updated_at text, payload_json text);
            insert into orchestration_v2_projection_threads values ('codex-thread', 'Codex task', '2026-10-26', null);
            insert into orchestration_v2_projection_provider_threads values
              ('c1', 'codex-thread', 'codex', 'codex', '2026-10-26', '{"nativeThreadRef":{"nativeId":"codex-id"}}');
            """);
        var codex = new T3ThreadTitles(StateFile, "codex");
        Assert.True(codex.Refresh());
        Assert.Equal("Codex task", codex.TitleFor("codex-id"));
        Assert.False(codex.IsFinished("codex-id"));
    }

    [Fact]
    public void Missing_database_reads_nothing() =>
        Assert.False(new T3ThreadTitles(Path.Combine(_dir, "missing.sqlite")).Refresh());

    [Fact]
    public void Session_without_a_title_is_labelled_with_its_project()
    {
        var project = Directory.CreateDirectory(Path.Combine(Root, "project")).FullName;
        File.WriteAllText(Path.Combine(project, $"{Session}.jsonl"), $$$"""
            {"type":"user","cwd":"D:\\repo","gitBranch":"main","entrypoint":"cli","timestamp":"{{{DateTimeOffset.UtcNow:O}}}","message":{"role":"user","content":"hi"}}

            """);
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var registry = new AgentRegistry(Options.Create(new TankOptions()), clock);
        using var source = new ClaudeCodeSource(registry, Options.Create(new ClaudeCodeOptions { T3StateFile = "" }),
            new PermissionRequests(), clock, NullLogger<ClaudeCodeSource>.Instance);

        source.Poll([Root]);
        var agent = registry.Snapshot().Single(a => a.Id == Session);
        Assert.Equal("repo", agent.Label);
        Assert.Equal("CLI · main", agent.Detail);

        File.AppendAllText(Path.Combine(project, $"{Session}.jsonl"),
            $$"""{"type":"ai-title","aiTitle":"Claude's own title","sessionId":"{{Session}}"}""" + "\n");
        source.Poll([Root]);
        agent = registry.Snapshot().Single(a => a.Id == Session);
        Assert.Equal("Claude's own title", agent.Label);
        Assert.Equal("CLI · repo · main", agent.Detail);
    }

    [Fact]
    public void T3_session_shows_the_t3_title_and_follows_a_rename()
    {
        AddThread("t1", "Aquarium thread titles", $$"""{"resume":"{{Session}}"}""");
        var cwd = Path.Combine(_dir, ".t3", "worktrees", "repo", "t3code-1234").Replace("\\", "\\\\", StringComparison.Ordinal);
        var project = Directory.CreateDirectory(Path.Combine(Root, "project")).FullName;
        File.WriteAllText(Path.Combine(project, $"{Session}.jsonl"), $$$"""
            {"type":"user","cwd":"{{{cwd}}}","gitBranch":"HEAD","timestamp":"{{{DateTimeOffset.UtcNow:O}}}","message":{"role":"user","content":"hi"}}
            {"type":"ai-title","aiTitle":"Claude's own title","sessionId":"{{{Session}}}"}

            """);
        // Running according to the live-session registry: this test's own process.
        var sessions = Directory.CreateDirectory(Path.Combine(_dir, "sessions")).FullName;
        File.WriteAllText(Path.Combine(sessions, $"{Environment.ProcessId}.json"),
            $$"""{"pid":{{Environment.ProcessId}},"sessionId":"{{Session}}","status":"idle"}""");

        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var registry = new AgentRegistry(Options.Create(new TankOptions()), clock);
        var options = new ClaudeCodeOptions { T3StateFile = StateFile, T3PollInterval = TimeSpan.FromSeconds(5) };
        using var source = new ClaudeCodeSource(registry, Options.Create(options), new PermissionRequests(), clock,
            NullLogger<ClaudeCodeSource>.Instance);

        source.Poll([Root]);
        var agent = registry.Snapshot().Single(a => a.Id == Session);
        Assert.Equal("Aquarium thread titles", agent.Label);
        Assert.Equal("T3 Code · repo", agent.Detail);

        Execute("update projection_threads set title = 'Renamed in T3' where thread_id = 't1'");
        clock.Advance(TimeSpan.FromSeconds(5));
        source.Poll([Root]);
        agent = registry.Snapshot().Single(a => a.Id == Session);
        Assert.Equal("Renamed in T3", agent.Label);
        Assert.Equal("T3 Code · repo", agent.Detail);
    }

    [Fact]
    public void T3_session_outside_the_default_worktrees_folder_is_known_by_its_title()
    {
        AddThread("t1", "Aquarium thread titles", $$"""{"resume":"{{Session}}"}""");
        // T3 Code's worktreesDirectory setting moves them anywhere.
        var cwd = Path.Combine(_dir, "T3", "Worktrees", "repo", "t3-1234").Replace("\\", "\\\\", StringComparison.Ordinal);
        var project = Directory.CreateDirectory(Path.Combine(Root, "project")).FullName;
        File.WriteAllText(Path.Combine(project, $"{Session}.jsonl"), $$$"""
            {"type":"user","cwd":"{{{cwd}}}","gitBranch":"t3/aquarium","entrypoint":"sdk-ts","timestamp":"{{{DateTimeOffset.UtcNow:O}}}","message":{"role":"user","content":"hi"}}
            {"type":"ai-title","aiTitle":"Claude's own title","sessionId":"{{{Session}}}"}

            """);

        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var registry = new AgentRegistry(Options.Create(new TankOptions()), clock);
        using var source = new ClaudeCodeSource(registry, Options.Create(new ClaudeCodeOptions { T3StateFile = StateFile }),
            new PermissionRequests(), clock, NullLogger<ClaudeCodeSource>.Instance);

        source.Poll([Root]);
        var agent = registry.Snapshot().Single(a => a.Id == Session);
        Assert.Equal("Aquarium thread titles", agent.Label);
        Assert.Equal("T3 Code · repo · t3/aquarium", agent.Detail);
    }
}
