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

    private static SystemMetricsSnapshot Snapshot(DateTimeOffset time) => new(time,
        new CpuMetric(10, 8, MetricAvailability.Supported),
        new MemoryMetric(16_000, 8_000, 50, MetricAvailability.Supported),
        new DiskMetric(10, 2, MetricAvailability.Supported), []);
}
