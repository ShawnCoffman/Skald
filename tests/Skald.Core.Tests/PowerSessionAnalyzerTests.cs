using Skald.Core.Models;
using Skald.Recorder;
using Xunit;

namespace Skald.Core.Tests;

public sealed class PowerSessionAnalyzerTests
{
    [Fact]
    public void ComparesMeasuredBatteryDrawWithoutAssigningWattsToProcesses()
    {
        var baseline = PowerSessionSummary.From(Document(9, 2));
        var current = PowerSessionSummary.From(Document(14, 8));
        Assert.Equal(9, baseline.AverageDischargeWatts);
        Assert.Equal(14, current.PeakDischargeWatts);
        Assert.InRange(current.FullChargeRuntimeHours!.Value, 3.999, 4.001);
        var comparison = PowerSessionComparison.Describe(baseline, current);
        Assert.Contains("9.0 → 14.0 W", comparison, StringComparison.Ordinal);
        Assert.Contains("Teams.exe", comparison, StringComparison.Ordinal);
        Assert.Contains("not measured per-process watts", comparison, StringComparison.Ordinal);
    }

    [Fact]
    public void DoesNotInventSystemDrawFromAcSensors()
    {
        var document = Document(12, 2);
        var ac = document with { Samples = document.Samples.Select(sample => sample with
            { Power = sample.Power with { Source = PowerSource.AC } }).ToArray() };
        Assert.Null(PowerSessionSummary.From(ac).AverageDischargeWatts);
    }

    [Fact]
    public void RunPowerUsesElapsedTimeAndExcludesLongGaps()
    {
        var start = DateTimeOffset.UtcNow;
        var offsets = new[] { 0, 2, 4, 60, 62 };
        var watts = new[] { 10d, 10d, 20d, 20d, 20d };
        var samples = offsets.Select((seconds, index) => new SystemMetricsSnapshot(start.AddSeconds(seconds),
            new CpuMetric(0, 8, MetricAvailability.Supported),
            new MemoryMetric(1, 1, 0, MetricAvailability.Supported),
            new DiskMetric(0, 0, MetricAvailability.Supported), [])
        {
            Power = new PowerMetric(PowerSource.Battery, 80, null, MetricAvailability.Supported, BatteryDischargeWatts: watts[index])
        }).ToArray();
        var document = new SessionDocument(new SessionMetadata(Guid.NewGuid(), "Skald", "1", "test", "Windows",
            start, start.AddSeconds(62), 2), samples, []);
        var summary = PowerSessionSummary.From(document);

        Assert.Equal(6, summary.BatteryDuration.TotalSeconds);
        Assert.Equal(15, summary.AverageDischargeWatts);
        Assert.Equal(0.025, summary.EstimatedBatteryEnergyWh);
        Assert.InRange(summary.RunCoveragePercent, 9.67, 9.68);
    }

    private static SessionDocument Document(double draw, double processCpu)
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-30);
        var samples = Enumerable.Range(0, 31).Select(index => new SystemMetricsSnapshot(start.AddMinutes(index),
            new CpuMetric(processCpu, 8, MetricAvailability.Supported),
            new MemoryMetric(16_000, 8_000, 50, MetricAvailability.Supported),
            new DiskMetric(10, 2, MetricAvailability.Supported),
            [new ProcessMetric(1, start, "Teams.exe", null, processCpu, 100, 80)])
        {
            Power = new PowerMetric(PowerSource.Battery, 80, null, MetricAvailability.Supported, BatteryDischargeWatts: draw)
            { BatteryFullChargeCapacityWh = 56, BatteryDesignCapacityWh = 57, BatteryRemainingCapacityWh = 42, DisplayBrightnessPercent = 50 }
        }).ToArray();
        return new(new SessionMetadata(Guid.NewGuid(), "Skald", "1", "test", "Windows", start, start.AddMinutes(30), 2), samples, []);
    }
}
