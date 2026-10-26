using AgentAquarium.Domain;

namespace AgentAquarium.Tank;

/// <summary>Holds the latest usage report. Sources write, the stream reads.</summary>
public sealed class UsageBoard : IUsageSink
{
    private readonly Lock _gate = new();
    private volatile UsageSnapshot? _current;
    private readonly Dictionary<string, UsageSnapshot> _bySource = new(StringComparer.Ordinal);

    public UsageSnapshot? Current => _current;

    public IReadOnlyList<UsageSnapshot> Snapshot()
    {
        lock (_gate)
            return _bySource.Values.ToList();
    }

    /// <summary>Each source keeps its latest report; replayed history cannot replace newer usage.</summary>
    public void Report(UsageSnapshot usage)
    {
        lock (_gate)
        {
            if (_bySource.TryGetValue(usage.Source, out var previous) && usage.AsOf < previous.AsOf)
                return;
            _bySource[usage.Source] = usage;
            if (_current is null || usage.AsOf >= _current.AsOf)
                _current = usage;
        }
    }
}
