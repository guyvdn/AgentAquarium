using System.Text.Json;
using AgentAquarium.Domain;

namespace AgentAquarium.Sources.Codex;

/// <summary>Account limits carried by Codex token_count events; no network requests.</summary>
internal static class CodexUsage
{
    public static UsageSnapshot? Parse(JsonElement payload, DateTimeOffset at)
    {
        if (payload.ValueKind != JsonValueKind.Object || !payload.TryGetProperty("rate_limits", out var limits) || limits.ValueKind != JsonValueKind.Object)
            return null;
        // Some models have a separate quota. Only show the account's Codex limit in this chest.
        if (limits.TryGetProperty("limit_id", out var id) && id.ValueKind == JsonValueKind.String && id.GetString() != "codex")
            return null;
        UsageWindow? fiveHour = null;
        UsageWindow? sevenDay = null;
        foreach (var name in new[] { "primary", "secondary" })
        {
            if (!limits.TryGetProperty(name, out var window) || window.ValueKind != JsonValueKind.Object
                || !window.TryGetProperty("window_minutes", out var minutes) || minutes.ValueKind != JsonValueKind.Number || !minutes.TryGetInt32(out var duration)
                || !window.TryGetProperty("used_percent", out var percent) || percent.ValueKind != JsonValueKind.Number || !percent.TryGetDouble(out var used)
                || !double.IsFinite(used) || used is < 0 or > 100)
                continue;
            DateTimeOffset? resets = window.TryGetProperty("resets_at", out var reset)
                && reset.ValueKind == JsonValueKind.Number && reset.TryGetInt64(out var seconds)
                && seconds >= 0 && seconds <= DateTimeOffset.MaxValue.ToUnixTimeSeconds()
                ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
            if (duration == 300)
                fiveHour = new UsageWindow(used, resets);
            else if (duration == 10080)
                sevenDay = new UsageWindow(used, resets);
        }
        return fiveHour is null ? null : new UsageSnapshot(fiveHour, sevenDay, at, CodexSource.SourceName);
    }
}
