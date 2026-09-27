using System.Diagnostics;
using Skald.Core.Models;

namespace Skald.Collectors;

internal sealed class PhysicalDiskCollector : IDisposable
{
    private static readonly string[] Counters = ["% Disk Time", "Disk Read Bytes/sec", "Disk Write Bytes/sec",
        "Disk Reads/sec", "Disk Writes/sec", "Avg. Disk sec/Read", "Avg. Disk sec/Write", "Current Disk Queue Length"];
    private readonly Dictionary<string, PerformanceCounter?[]> _disks = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _discoveredAt;

    public IReadOnlyList<PhysicalDiskMetric> Sample()
    {
        if (DateTimeOffset.UtcNow - _discoveredAt > TimeSpan.FromMinutes(1)) Discover();
        return _disks.Select(pair =>
        {
            var values = pair.Value.Select(Read).ToArray();
            return new PhysicalDiskMetric(pair.Key, values[0], values[1], values[2], values[3], values[4],
                values[5] * 1000, values[6] * 1000, values[7]);
        }).ToArray();
    }

    private void Discover()
    {
        _discoveredAt = DateTimeOffset.UtcNow;
        try
        {
            var names = new PerformanceCounterCategory("PhysicalDisk").GetInstanceNames()
                .Where(name => name != "_Total").ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var stale in _disks.Keys.Where(name => !names.Contains(name)).ToArray())
            {
                foreach (var counter in _disks[stale]) counter?.Dispose();
                _disks.Remove(stale);
            }
            foreach (var name in names.Where(name => !_disks.ContainsKey(name)))
                _disks[name] = Counters.Select(counter => Create(counter, name)).ToArray();
        }
        catch (InvalidOperationException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static PerformanceCounter? Create(string counter, string instance)
    {
        try { return new PerformanceCounter("PhysicalDisk", counter, instance, readOnly: true); }
        catch (InvalidOperationException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static double? Read(PerformanceCounter? counter)
    {
        if (counter is null) return null;
        try { var value = counter.NextValue(); return double.IsFinite(value) && value >= 0 ? value : null; }
        catch (InvalidOperationException) { return null; }
    }

    public void Dispose()
    {
        foreach (var counters in _disks.Values)
        foreach (var counter in counters) counter?.Dispose();
        _disks.Clear();
    }
}
