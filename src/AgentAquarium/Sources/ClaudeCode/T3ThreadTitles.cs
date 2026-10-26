using Microsoft.Data.Sqlite;

namespace AgentAquarium.Sources.ClaudeCode;

/// <summary>
/// The titles T3 Code shows for its threads, by native session id. T3 Code names threads itself
/// rather than taking Claude's <c>ai-title</c>, so without this the Schools list shows a title that
/// cannot be found in T3 Code's sidebar. Read from its state database. Undocumented, two layouts:
/// <list type="bullet">
/// <item><c>statev2.sqlite</c> (T3 Code since 2026-10-20): titles in <c>orchestration_v2_projection_threads</c>.
/// A thread run since then names its Claude session in <c>orchestration_v2_projection_provider_threads</c>
/// (<c>payload_json</c> → <c>nativeThreadRef.nativeId</c>); one that has not run again only in the
/// <c>provider_session_runtime</c> rows imported from the old database. The old <c>projection_threads</c>
/// is imported too but no longer updated, so its titles go stale.</item>
/// <item><c>state.sqlite</c> (before): <c>projection_threads.title</c>, joined to the Claude session the
/// thread resumes (<c>provider_session_runtime.resume_cursor_json</c> → <c>resume</c>).</item>
/// </list>
/// After the update T3 Code leaves <c>state.sqlite</c> behind, frozen, so the newer file wins when both exist.
/// For Codex it also reads which threads are finished (<see cref="IsFinished"/>), since writer locks cannot tell.
/// </summary>
/// <param name="stateFile">The database to read; null picks T3 Code's own, empty turns titles off.</param>
/// <param name="driver">The native provider: claudeAgent or codex. Legacy titles are Claude-only.</param>
public sealed class T3ThreadTitles(string? stateFile, string driver = "claudeAgent")
{
    private const string V2Threads = "orchestration_v2_projection_threads";

    // A session id found by both parts: the v2 row (sorted last) wins.
    private const string V2Query = $"""
        select json_extract(r.resume_cursor_json, '$.resume'), t.title, 0, t.updated_at
        from provider_session_runtime r
        join {V2Threads} t on t.thread_id = r.thread_id
        where $driver = 'claudeAgent' and t.deleted_at is null and json_valid(r.resume_cursor_json)
        union all
        select json_extract(p.payload_json, '$.nativeThreadRef.nativeId'), t.title, 1, p.updated_at
        from orchestration_v2_projection_provider_threads p
        join {V2Threads} t on t.thread_id = p.thread_id
        where t.deleted_at is null and coalesce(p.driver, p.provider) = $driver and json_valid(p.payload_json)
        order by 3, 4
        """;

    private const string LegacyQuery = """
        select json_extract(r.resume_cursor_json, '$.resume'), t.title
        from provider_session_runtime r
        join projection_threads t on t.thread_id = r.thread_id
        where $driver = 'claudeAgent' and t.deleted_at is null and json_valid(r.resume_cursor_json)
        order by t.updated_at
        """;

    private const string HasV2Query = $"select 1 from sqlite_master where type = 'table' and name = '{V2Threads}'";

    // T3 Code's shared Codex app-server holds a writer lock for every conversation it has loaded, so the
    // lock cannot tell a finished one apart. Finished here: deleted, archived, or idle and either settled
    // in the sidebar, a delegated subagent, or replaced by another provider on the same thread. For a
    // session on several provider threads, the thread's active one wins, then the latest.
    private const string CodexFinishedQuery = $"""
        select json_extract(p.payload_json, '$.nativeThreadRef.nativeId'),
               coalesce(t.deleted_at is not null or t.archived_at is not null
                   or (p.status = 'idle' and (json_extract(t.payload_json, '$.settledOverride') = 'settled'
                       or json_extract(t.payload_json, '$.lineage.relationshipToParent') = 'subagent'
                       or t.active_provider_thread_id <> p.provider_thread_id)), 0)
        from orchestration_v2_projection_provider_threads p
        join {V2Threads} t on t.thread_id = p.thread_id
        where coalesce(p.driver, p.provider) = 'codex' and json_valid(p.payload_json) and json_valid(t.payload_json)
        order by coalesce(t.active_provider_thread_id = p.provider_thread_id, 0), p.updated_at
        """;

    private static readonly string[] DefaultStateFiles = ["statev2.sqlite", "state.sqlite"];

    private Dictionary<string, string> _bySession = new(StringComparer.OrdinalIgnoreCase);

    private HashSet<string> _finished = new(StringComparer.OrdinalIgnoreCase);

    private static string UserData =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".t3", "userdata");

    /// <summary>T3 Code's database, resolved on every read so an update while the aquarium runs is followed.</summary>
    private string? StateFile =>
        stateFile ?? DefaultStateFiles.Select(f => Path.Combine(UserData, f)).FirstOrDefault(File.Exists);

    public string? TitleFor(string sessionId) => _bySession.GetValueOrDefault(sessionId);

    /// <summary>True for a Codex session whose T3 Code thread is finished, however its writer lock looks.</summary>
    public bool IsFinished(string sessionId) => _finished.Contains(sessionId);

    /// <summary>
    /// Reads the titles, and for Codex the finished threads, again. True when any changed; a missing or busy
    /// database keeps the last ones, and so does a failed finished read, without holding back the titles.
    /// </summary>
    public bool Refresh()
    {
        var file = StateFile;
        if (string.IsNullOrEmpty(file) || !File.Exists(file))
            return false;

        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var finished = _finished;
        try
        {
            // Not pooled, so no handle is kept on T3 Code's database between reads.
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = file,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            // A busy database is retried until the timeout (30 s by default) and Poll waits for it.
            command.CommandTimeout = 1;
            command.Parameters.AddWithValue("$driver", driver);
            command.CommandText = HasV2Query;
            var hasV2 = command.ExecuteScalar() is not null;
            if (hasV2)
                command.CommandText = V2Query;
            else
                command.CommandText = LegacyQuery;
            using (var reader = command.ExecuteReader())
                while (reader.Read())
                    if (!reader.IsDBNull(0) && !reader.IsDBNull(1))
                        titles[reader.GetString(0)] = reader.GetString(1); // a session resumed twice: the latest thread wins
            if (hasV2 && driver == "codex")
                finished = ReadFinished(command) ?? _finished;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false; // busy, a schema we do not know, or no native SQLite: keep what we had
        }

        if (finished.SetEquals(_finished) && titles.Count == _bySession.Count
            && titles.All(t => _bySession.GetValueOrDefault(t.Key) == t.Value))
            return false;
        _bySession = titles;
        _finished = finished;
        return true;
    }

    /// <summary>Null when the query fails: a schema without one of its columns must not cost the titles.</summary>
    private static HashSet<string>? ReadFinished(SqliteCommand command)
    {
        var finished = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            command.CommandText = CodexFinishedQuery;
            using var reader = command.ExecuteReader();
            while (reader.Read())
                if (!reader.IsDBNull(0))
                {
                    if (reader.GetBoolean(1))
                        finished.Add(reader.GetString(0));
                    else
                        finished.Remove(reader.GetString(0));
                }
            return finished;
        }
        catch (SqliteException)
        {
            return null;
        }
    }
}
