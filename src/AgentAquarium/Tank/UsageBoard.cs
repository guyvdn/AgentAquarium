using AgentAquarium.Domain;

namespace AgentAquarium.Tank;

/// <summary>Holds the latest usage report. Sources write, the stream reads.</summary>
public sealed class UsageBoard : IUsageSink
{
    private readonly Lock _gate = new();
    private volatile UsageSnapshot? _current;

    public UsageSnapshot? Current => _current;

    /// <summary>An older report than the one held is ignored, so two sources cannot flip it back and forth.</summary>
    public void Report(UsageSnapshot usage)
    {
        lock (_gate)
        {
            if (_current is null || usage.AsOf >= _current.AsOf)
                _current = usage;
        }
    }
}
