namespace AgentAquarium.Domain;

// Plan rate limits, as source-neutral as the agents: a source reports what it knows,
// the tank shows the latest report.

/// <summary>One rate-limit window: how much of it is used, 0–100, and when it resets.</summary>
public sealed record UsageWindow(double UsedPercent, DateTimeOffset? ResetsAt);

/// <summary>The plan's rate limits as last reported. A window the source does not know is null.</summary>
/// <param name="AsOf">When the numbers were measured, not when they were read.</param>
public sealed record UsageSnapshot(UsageWindow? FiveHour, UsageWindow? SevenDay, DateTimeOffset AsOf, string Source);

public interface IUsageSink
{
    void Report(UsageSnapshot usage);
}
