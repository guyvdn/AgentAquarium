using System.Text.Json;
using AgentAquarium.Domain;
using AgentAquarium.Sources.Codex;
using AgentAquarium.Tank;

namespace AgentAquarium.Tests;

public sealed class CodexUsageTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.FromUnixTimeSeconds(1793000000);

    private static UsageSnapshot? Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return CodexUsage.Parse(doc.RootElement, At);
    }

    [Fact]
    public void Parses_account_windows_by_duration_and_retains_the_measurement_time()
    {
        var usage = Parse("""
            {"info":null,"rate_limits":{"limit_id":"codex",
              "primary":{"used_percent":12.5,"window_minutes":300,"resets_at":1793026339},
              "secondary":{"used_percent":4,"window_minutes":10080,"resets_at":1793613139}}}
            """);
        Assert.NotNull(usage);
        Assert.Equal("codex", usage.Source);
        Assert.Equal(At, usage.AsOf);
        Assert.Equal(new UsageWindow(12.5, DateTimeOffset.FromUnixTimeSeconds(1793026339)), usage.FiveHour);
        Assert.Equal(4, usage.SevenDay!.UsedPercent);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("""{"rate_limits":null}""")]
    [InlineData("""{"rate_limits":{"limit_id":"other-model","primary":{"used_percent":10,"window_minutes":300}}}""")]
    [InlineData("""{"rate_limits":{"primary":{"used_percent":10,"window_minutes":60}}}""")]
    [InlineData("""{"rate_limits":{"primary":{"used_percent":101,"window_minutes":300}}}""")]
    [InlineData("""{"rate_limits":{"primary":{"used_percent":"10","window_minutes":300}}}""")]
    [InlineData("""{"rate_limits":{"primary":{"used_percent":10,"window_minutes":"300"}}}""")]
    public void Unknown_or_malformed_limits_are_not_shown(string json) => Assert.Null(Parse(json));

    [Fact]
    public void Missing_weekly_and_invalid_reset_are_tolerated()
    {
        var usage = Parse("""{"rate_limits":{"primary":{"used_percent":0,"window_minutes":300,"resets_at":99999999999999}}}""");
        Assert.Equal(new UsageWindow(0, null), usage!.FiveHour);
        Assert.Null(usage.SevenDay);
    }

    [Fact]
    public void Each_provider_keeps_its_latest_usage_independently()
    {
        var board = new UsageBoard();
        var claude = new UsageSnapshot(new UsageWindow(80, null), null, At, "claude-code");
        var codex = new UsageSnapshot(new UsageWindow(10, null), null, At.AddMinutes(-5), "codex");
        board.Report(claude);
        board.Report(codex);
        board.Report(codex with { AsOf = At.AddHours(-1), FiveHour = new UsageWindow(90, null) });
        Assert.Equal(2, board.Snapshot().Count);
        Assert.Contains(claude, board.Snapshot());
        Assert.Contains(codex, board.Snapshot());
        Assert.Same(claude, board.Current); // legacy single-report endpoint
    }
}
