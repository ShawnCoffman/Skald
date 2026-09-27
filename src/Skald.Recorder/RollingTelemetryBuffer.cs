using Skald.Core.Models;

namespace Skald.Recorder;

public sealed class RollingTelemetryBuffer
{
    private readonly object _gate = new();
    private readonly TimeSpan _retention;
    private readonly Queue<SystemMetricsSnapshot> _samples = new();

    public RollingTelemetryBuffer(TimeSpan retention)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retention, TimeSpan.Zero);

        _retention = retention;
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _samples.Count;
            }
        }
    }

    public void Add(SystemMetricsSnapshot snapshot)
    {
        lock (_gate)
        {
            _samples.Enqueue(snapshot);
            Trim(snapshot.Timestamp);
        }
    }

    public IReadOnlyList<SystemMetricsSnapshot> GetSnapshot()
    {
        lock (_gate)
        {
            return _samples.ToArray();
        }
    }

    public IReadOnlyList<SystemMetricsSnapshot> GetWindow(DateTimeOffset center, TimeSpan before, TimeSpan after)
    {
        if (before < TimeSpan.Zero || after < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(before));
        }

        var start = center - before;
        var end = center + after;
        lock (_gate)
        {
            return _samples.Where(sample => sample.Timestamp >= start && sample.Timestamp <= end).ToArray();
        }
    }

    private void Trim(DateTimeOffset newestTimestamp)
    {
        var cutoff = newestTimestamp - _retention;
        while (_samples.TryPeek(out var oldest) && oldest.Timestamp < cutoff)
        {
            _samples.Dequeue();
        }
    }
}
