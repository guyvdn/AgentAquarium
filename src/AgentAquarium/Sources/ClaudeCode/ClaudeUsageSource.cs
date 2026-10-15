using System.Text.Json;
using AgentAquarium.Domain;
using Microsoft.Extensions.Options;

namespace AgentAquarium.Sources.ClaudeCode;

/// <summary>
/// Reads the plan's 5-hour and weekly usage from the copy Claude Code caches in its global
/// config file (<c>cachedUsageUtilization</c> in <c>~/.claude.json</c>).
/// <para>
/// Deliberately not the <c>/api/oauth/usage</c> endpoint that cache is filled from: it is
/// rate limited hard and shares its budget with Claude Code's own <c>/usage</c>, so a second
/// poller can lock both out for hours. Reading the cache costs no requests; the price is that
/// the numbers are only as fresh as Claude Code's last fetch, which <see cref="UsageSnapshot.AsOf"/> carries.
/// </para>
/// </summary>
public sealed partial class ClaudeUsageSource(
    IUsageSink sink,
    IOptions<ClaudeCodeOptions> options,
    TimeProvider clock,
    ILogger<ClaudeUsageSource> logger) : BackgroundService
{
    private static readonly long MaxUnixMs = DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    private readonly ClaudeCodeOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var path = _options.UsageFile ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json");
        if (!_options.Enabled || path.Length == 0)
            return;

        LogWatching(logger, path);
        DateTime? lastWrite = null;
        using var timer = new PeriodicTimer(_options.UsagePollInterval, clock);
        do
        {
            try
            {
                // The file is large and rewritten often for unrelated reasons; only parse it when it changed.
                var written = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : (DateTime?)null;
                if (written is null || written == lastWrite)
                    continue;

                // Unparseable usually means caught mid-write: leave lastWrite alone so the next tick retries.
                if (Read(path) is { } usage)
                {
                    sink.Report(usage);
                    lastWrite = written;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LogReadFailed(logger, path, ex);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogPollFailed(logger, ex);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>
    /// Parses <c>{"cachedUsageUtilization": {"fetchedAtMs": …, "utilization": {"five_hour": {"utilization": 32, "resets_at": "…"}, "seven_day": {…}}}}</c>.
    /// The shape is undocumented (checked against Claude Code 2.1.300); anything unexpected yields null.
    /// </summary>
    internal static UsageSnapshot? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return Parse(doc);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static UsageSnapshot? Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = JsonDocument.Parse(stream);
            return Parse(doc);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static UsageSnapshot? Parse(JsonDocument doc)
    {
        if (doc.RootElement.ValueKind != JsonValueKind.Object
            || !doc.RootElement.TryGetProperty("cachedUsageUtilization", out var cache)
            || cache.ValueKind != JsonValueKind.Object
            || !cache.TryGetProperty("fetchedAtMs", out var fetched) || fetched.ValueKind != JsonValueKind.Number
            || !fetched.TryGetInt64(out var fetchedMs) || fetchedMs < 0 || fetchedMs > MaxUnixMs
            || !cache.TryGetProperty("utilization", out var utilization) || utilization.ValueKind != JsonValueKind.Object)
            return null;

        // The chest is the 5-hour window; without it there is nothing to show.
        var fiveHour = Window(utilization, "five_hour");
        return fiveHour is null
            ? null
            : new UsageSnapshot(fiveHour, Window(utilization, "seven_day"), DateTimeOffset.FromUnixTimeMilliseconds(fetchedMs), ClaudeCodeSource.SourceName);
    }

    private static UsageWindow? Window(JsonElement utilization, string name)
    {
        if (!utilization.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object
            || !window.TryGetProperty("utilization", out var used) || used.ValueKind != JsonValueKind.Number
            || !used.TryGetDouble(out var usedPercent) || !double.IsFinite(usedPercent))
            return null;

        DateTimeOffset? resetsAt = window.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String
                                   && r.TryGetDateTimeOffset(out var parsed)
            ? parsed
            : null;
        return new UsageWindow(usedPercent, resetsAt);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Reading plan usage from {Path}")]
    private static partial void LogWatching(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Reading plan usage failed; retrying next tick")]
    private static partial void LogPollFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Could not read {Path}; retrying next tick")]
    private static partial void LogReadFailed(ILogger logger, string path, Exception exception);
}
