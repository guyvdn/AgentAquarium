using Microsoft.Data.Sqlite;

namespace AgentAquarium.Sources.ClaudeCode;

/// <summary>
/// The titles T3 Code shows for its threads, by Claude session id. T3 Code names threads itself
/// rather than taking Claude's <c>ai-title</c>, so without this the Schools list shows a title that
/// cannot be found in T3 Code's sidebar. Read from its state database: <c>projection_threads.title</c>,
/// joined to the Claude session the thread resumes (<c>provider_session_runtime.resume_cursor_json</c>
/// → <c>resume</c>). Undocumented, checked against T3 Code's schema on 2026-10-16.
/// </summary>
public sealed class T3ThreadTitles(string? stateFile)
{
    private const string Query = """
        select json_extract(r.resume_cursor_json, '$.resume'), t.title
        from provider_session_runtime r
        join projection_threads t on t.thread_id = r.thread_id
        where t.deleted_at is null and json_valid(r.resume_cursor_json)
        order by t.updated_at
        """;

    private Dictionary<string, string> _bySession = new(StringComparer.OrdinalIgnoreCase);

    public static string DefaultStateFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".t3", "userdata", "state.sqlite");

    public string? TitleFor(string sessionId) => _bySession.GetValueOrDefault(sessionId);

    /// <summary>Reads the titles again. True when any changed; a missing or busy database keeps the last ones.</summary>
    public bool Refresh()
    {
        if (string.IsNullOrEmpty(stateFile) || !File.Exists(stateFile))
            return false;

        var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            // Not pooled, so no handle is kept on T3 Code's database between reads.
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = stateFile,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false,
            }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = Query;
            // A busy database is retried until the timeout (30 s by default) and Poll waits for it.
            command.CommandTimeout = 1;
            using var reader = command.ExecuteReader();
            while (reader.Read())
                if (!reader.IsDBNull(0) && !reader.IsDBNull(1))
                    titles[reader.GetString(0)] = reader.GetString(1); // a session resumed twice: the latest thread wins
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false; // busy, a schema we do not know, or no native SQLite: keep what we had
        }

        if (titles.Count == _bySession.Count && titles.All(t => _bySession.GetValueOrDefault(t.Key) == t.Value))
            return false;
        _bySession = titles;
        return true;
    }
}
