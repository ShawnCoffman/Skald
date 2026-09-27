using System.Diagnostics;
using Skald.Core.Models;

namespace Skald.Collectors;

internal sealed class WindowsPowerTelemetryCollector : IDisposable
{
    private readonly List<(string Instance, PerformanceCounter Counter)> _meters = [];
    public WindowsPowerTelemetryCollector()
    {
        try
        {
            if (!PerformanceCounterCategory.Exists("Power Meter")) return;
            foreach (var instance in new PerformanceCounterCategory("Power Meter").GetInstanceNames())
                _meters.Add((instance, new PerformanceCounter("Power Meter", "Power", instance, true)));
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
    }
    public IReadOnlyList<SensorMetric> Sample()
    {
        var result = new List<SensorMetric>();
        foreach (var (instance, counter) in _meters)
        {
            double? watts = null;
            try { var value = counter.NextValue(); if (float.IsFinite(value) && value >= 0) watts = value; }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            result.Add(new SensorMetric($"windows/power-meter/{instance}", instance, "Hardware power meter", "Power", "W",
                watts, "Windows Power Meter", "Unidentified hardware domain", "Not attributed to CPU package or whole system."));
        }
        return result;
    }
    public void Dispose() { foreach (var meter in _meters) meter.Counter.Dispose(); }
}
