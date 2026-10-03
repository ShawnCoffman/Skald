using Skald.Core.Models;
using Skald.Diagnostics;
using Xunit;

namespace Skald.Core.Tests;

public sealed class WindowDiagnosticAnalyzerTests
{
    [Fact]
    public void ReportsSustainedPhysicalDiskLatencyWithDiskIdentity()
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-1);
        var samples = Enumerable.Range(0, 8).Select(index => Snapshot(start.AddSeconds(index * 2)) with
        {
            Disk = new DiskMetric(50, 30, MetricAvailability.Supported)
            {
                PhysicalDisks = [new PhysicalDiskMetric("1 C:", 50, 0, 0, 4, 0, 75, null, 2)]
            }
        }).ToArray();
        Assert.Contains(WindowDiagnosticAnalyzer.Analyze(samples), finding => finding.Title == "High read latency · 1 C:");
    }

    [Fact]
    public void GapsBreakSustainedRules()
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-1);
        var seconds = new[] { 0, 2, 4, 6, 20, 22, 24, 26 };
        var samples = seconds.Select(second => Snapshot(start.AddSeconds(second)) with
        {
            Cpu = new CpuMetric(95, 8, MetricAvailability.Supported)
        }).ToArray();
        Assert.DoesNotContain(WindowDiagnosticAnalyzer.Analyze(samples), finding => finding.Title == "Sustained CPU saturation");
    }

    [Fact]
    public void ReportsSustainedPlatformThermalLimitButNotWhenUnlimited()
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-1);
        SensorMetric Limit(string kind, double value) => new($"windows/thermal-zone/TZ00/{kind}", "TZ00", kind, "Limit", "%", value, "test", "Thermal limit");
        var limited = Enumerable.Range(0, 20).Select(index => Snapshot(start.AddSeconds(index * 2)) with { Sensors = [Limit("passive-limit", 70), Limit("throttle-reasons", 0)] }).ToArray();
        var finding = Assert.Single(WindowDiagnosticAnalyzer.Analyze(limited), item => item.Title.StartsWith("Platform thermal limit", StringComparison.Ordinal));
        Assert.Contains("TZ00", finding.Title, StringComparison.Ordinal);
        var unlimited = limited.Select(sample => sample with { Sensors = [Limit("passive-limit", 100), Limit("throttle-reasons", 0)] }).ToArray();
        Assert.DoesNotContain(WindowDiagnosticAnalyzer.Analyze(unlimited), item => item.Title.StartsWith("Platform thermal limit", StringComparison.Ordinal));
        var reasons = limited.Select(sample => sample with { Sensors = [Limit("passive-limit", 100), Limit("throttle-reasons", 4)] }).ToArray();
        Assert.Contains(WindowDiagnosticAnalyzer.Analyze(reasons), item => item.Title.StartsWith("Platform thermal limit", StringComparison.Ordinal));
    }

    [Fact]
    public void EffectiveClockAppearsInSensorCatalogOnlyWhenMeasured()
    {
        var measured = Snapshot(DateTimeOffset.UtcNow) with { Cpu = new CpuMetric(10, 8, MetricAvailability.Supported) { EffectiveMegahertz = 4700 } };
        Assert.Equal(4700, SensorCatalog.GetSensors(measured).Single(sensor => sensor.Id == "cpu/effective/clock").Value);
        var missing = SensorCatalog.GetSensors(Snapshot(DateTimeOffset.UtcNow));
        Assert.Null(missing.Single(sensor => sensor.Id == "cpu/effective/unavailable").Value);
        Assert.DoesNotContain(missing, sensor => sensor.Id == "cpu/effective/clock");
    }

    private static SystemMetricsSnapshot Snapshot(DateTimeOffset time) => new(time,
        new CpuMetric(10, 8, MetricAvailability.Supported),
        new MemoryMetric(16_000, 8_000, 50, MetricAvailability.Supported),
        new DiskMetric(10, 2, MetricAvailability.Supported), []);
}
