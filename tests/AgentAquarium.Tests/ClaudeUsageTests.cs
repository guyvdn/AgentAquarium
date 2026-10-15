using System.Globalization;
using AgentAquarium.Domain;
using AgentAquarium.Sources.ClaudeCode;
using AgentAquarium.Tank;

namespace AgentAquarium.Tests;

// Fixture is a trimmed copy of ~/.claude.json from Claude Code 2.1.300.
public class ClaudeUsageTests
{
    [Fact]
    public void Parses_both_windows_and_when_they_were_fetched()
    {
        var usage = ClaudeUsageSource.Parse("""
            {"numStartups":12,"cachedUsageUtilization":{"fetchedAtMs":1792053146427,"accountUuid":"x","utilization":{
              "five_hour":{"utilization":32,"resets_at":"2026-10-15T11:19:59.641448+00:00","limit_dollars":null},
              "seven_day":{"utilization":12.5,"resets_at":"2026-10-20T20:59:59.641466+00:00"},
              "seven_day_opus":null,"extra_usage":{"is_enabled":true}}}}
            """);

        Assert.NotNull(usage);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1792053146427), usage.AsOf);
        Assert.Equal(32, usage.FiveHour!.UsedPercent);
        Assert.Equal(DateTimeOffset.Parse("2026-10-15T11:19:59.641448Z", CultureInfo.InvariantCulture), usage.FiveHour.ResetsAt);
        Assert.Equal(12.5, usage.SevenDay!.UsedPercent);
    }

    [Fact]
    public void A_missing_window_is_null_and_a_missing_reset_is_tolerated()
    {
        var usage = ClaudeUsageSource.Parse("""
            {"cachedUsageUtilization":{"fetchedAtMs":1,"utilization":{"five_hour":{"utilization":5},"seven_day":null}}}
            """);

        Assert.Equal(new UsageWindow(5, null), usage!.FiveHour);
        Assert.Null(usage.SevenDay);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"cachedUsageUtilization":null}""")]
    [InlineData("""{"cachedUsageUtilization":{"utilization":{"five_hour":{"utilization":5}}}}""")]
    [InlineData("""{"cachedUsageUtilization":{"fetchedAtMs":1,"utilization":{"five_hour":{"utilization":null}}}}""")]
    [InlineData("""{"cachedUsageUtilization":{"fetchedAtMs":"123","utilization":{"five_hour":{"utilization":5}}}}""")]
    [InlineData("""{"cachedUsageUtilization":{"fetchedAtMs":null,"utilization":{"five_hour":{"utilization":5}}}}""")]
    [InlineData("""{"cachedUsageUtilization":{"fetchedAtMs":99999999999999999,"utilization":{"five_hour":{"utilization":5}}}}""")]
    [InlineData("""{"cachedUsageUtilization":{"fetchedAtMs":1,"utilization":{"five_hour":{"utilization":1e400}}}}""")]
    [InlineData("""{"cachedUsageUtilization":{"fetchedAtMs":1,"utilization":{"seven_day":{"utilization":5}}}}""")]
    [InlineData("{\"cachedUsageUtilization\":{\"fetchedAtMs\":1,\"utili")]
    [InlineData("not json")]
    [InlineData("[]")]
    public void Anything_unexpected_is_null(string json) => Assert.Null(ClaudeUsageSource.Parse(json));

    [Fact]
    public void The_board_keeps_the_newest_report()
    {
        var board = new UsageBoard();
        var newer = new UsageSnapshot(new UsageWindow(40, null), null, DateTimeOffset.UnixEpoch.AddHours(2), "a");
        var older = new UsageSnapshot(new UsageWindow(10, null), null, DateTimeOffset.UnixEpoch.AddHours(1), "b");

        board.Report(newer);
        board.Report(older);

        Assert.Same(newer, board.Current);
    }
}
