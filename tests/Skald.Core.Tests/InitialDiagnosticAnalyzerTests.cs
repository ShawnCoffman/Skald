using Skald.Core.Models;
using Skald.Diagnostics;
using Xunit;

namespace Skald.Core.Tests;

public sealed class InitialDiagnosticAnalyzerTests
{
    [Fact]
    public void ReportsLowAvailableMemoryWhenBothRelativeAndAbsoluteThresholdsAreMet()
    {
        const ulong gib = 1024UL * 1024 * 1024;
        var snapshot = new SystemMetricsSnapshot(
            DateTimeOffset.UtcNow,
            new CpuMetric(15, 8, MetricAvailability.Supported),
            new MemoryMetric(16 * gib, 800 * 1024UL * 1024, 95, MetricAvailability.Supported),
            new DiskMetric(12, 3, MetricAvailability.Supported),
            []);

        var findings = new InitialDiagnosticAnalyzer().Analyze(snapshot);

        var finding = Assert.Single(findings);
        Assert.Equal("Low available physical memory", finding.Title);
        Assert.Equal(PressureState.Elevated, finding.Severity);
    }

    [Fact]
    public void DoesNotTreatHighOccupancyAloneAsMemoryPressure()
    {
        const ulong gib = 1024UL * 1024 * 1024;
        var snapshot = new SystemMetricsSnapshot(
            DateTimeOffset.UtcNow,
            new CpuMetric(15, 8, MetricAvailability.Supported),
            new MemoryMetric(64 * gib, 6 * gib, 90.6, MetricAvailability.Supported),
            new DiskMetric(12, 3, MetricAvailability.Supported),
            []);

        Assert.Empty(new InitialDiagnosticAnalyzer().Analyze(snapshot));
    }

    [Fact]
    public void DoesNotReportFindingWhenInitialThresholdsAreNotMet()
    {
        var snapshot = new SystemMetricsSnapshot(
            DateTimeOffset.UtcNow,
            new CpuMetric(15, 8, MetricAvailability.Supported),
            new MemoryMetric(16_000, 8_000, 50, MetricAvailability.Supported),
            new DiskMetric(12, 3, MetricAvailability.Supported),
            []);

        var findings = new InitialDiagnosticAnalyzer().Analyze(snapshot);

        Assert.Empty(findings);
    }
}
