using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace AgentAquarium.Sources.ClaudeCode;

/// <summary>A running Claude Code process, as it registers itself in <c>&lt;config&gt;/sessions/&lt;pid&gt;.json</c>.</summary>
public sealed record LiveSession(
    string SessionId,
    int Pid,
    string? Name,
    string? Cwd,
    string? Entrypoint,
    string? Status,
    long? StatusUpdatedAt,
    string? ProcStart);

/// <summary>
/// Reads Claude Code's live-session registry, which says which sessions actually have a
/// running process. Transcript age alone cannot: a closed session's transcript looks the
/// same as an idle one's. Files are keyed by pid and pids get reused, so a file only
/// counts when its <c>procStart</c> matches the running process's start time.
/// </summary>
public static class LiveSessions
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The registry folder belonging to a <c>&lt;config&gt;/projects</c> root.</summary>
    public static string DirectoryFor(string projectsRoot) =>
        Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(projectsRoot))!, "sessions");

    /// <summary>Live sessions by session id, or null when the registry does not exist (older Claude Code).</summary>
    public static IReadOnlyDictionary<string, LiveSession>? Read(string projectsRoot)
    {
        var dir = DirectoryFor(projectsRoot);
        if (!Directory.Exists(dir))
            return null;

        var live = new Dictionary<string, LiveSession>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            var session = TryParse(file);
            if (session is not null && IsAlive(session))
                live[session.SessionId] = session;
        }
        return live;
    }

    private static LiveSession? TryParse(string file)
    {
        try
        {
            var session = JsonSerializer.Deserialize<LiveSession>(File.ReadAllText(file), Json);
            return session is { SessionId.Length: > 0, Pid: > 0 } ? session : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal static bool IsAlive(LiveSession session)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(session.Pid);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false; // no such pid
        }

        using (process)
        {
            try
            {
                if (process.HasExited)
                    return false;

                // On Windows procStart is the process creation time as a FILETIME.
                // Elsewhere its format differs; fall back to "the pid exists".
                return !OperatingSystem.IsWindows()
                       || session.ProcStart is null
                       || session.ProcStart == process.StartTime.ToFileTimeUtc().ToString(CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // The pid exists but we may not inspect it (access denied): give it the benefit of
                // the doubt rather than pulling a running session out of the tank.
                return true;
            }
        }
    }
}
