namespace Skald.Core.Models;

public sealed class TelemetryHistory
{
    private readonly List<SystemMetricsSnapshot> _samples = [];
    public IReadOnlyList<SystemMetricsSnapshot> Samples => _samples;

    public void Add(SystemMetricsSnapshot snapshot)
    {
        if (_samples.Count > 0 && snapshot.Timestamp < _samples[^1].Timestamp) _samples.Clear();
        if (_samples.Count > 0 && snapshot.Timestamp == _samples[^1].Timestamp) return;
        _samples.Add(snapshot);
        _samples.RemoveAll(sample => sample.Timestamp < snapshot.Timestamp.AddMinutes(-3));
    }

    public void Replace(IEnumerable<SystemMetricsSnapshot> samples)
    {
        _samples.Clear();
        foreach (var sample in samples.OrderBy(sample => sample.Timestamp)) Add(sample);
    }
}
