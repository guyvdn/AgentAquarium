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
            {"type":"user","cwd":"{{{cwd}}}","timestamp":"{{{DateTimeOffset.UtcNow:O}}}","message":{"role":"user","content":"hi"}}
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
}
