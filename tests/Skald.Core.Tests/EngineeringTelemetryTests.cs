using Skald.Core.Models;
using Skald.Diagnostics;
using Xunit;

namespace Skald.Core.Tests;

public sealed class EngineeringTelemetryTests
{
    private static SystemMetricsSnapshot Sample(DateTimeOffset time, double cpu = 95) => new(time,
        new CpuMetric(cpu, 8, MetricAvailability.Supported), new MemoryMetric(16_000_000_000, 8_000_000_000, 50, MetricAvailability.Supported),
        new DiskMetric(0, 0, MetricAvailability.Supported), []);

    [Fact]
    public void RepeatedRenderDoesNotShortenHistory()
    {
        var history = new TelemetryHistory();
        var start = DateTimeOffset.UtcNow;
        for (var index = 0; index <= 90; index++)
        {
            var snapshot = Sample(start.AddSeconds(index * 2));
            history.Add(snapshot); history.Add(snapshot);
        }
        Assert.Equal(91, history.Samples.Count);
        Assert.Equal(TimeSpan.FromMinutes(3), history.Samples[^1].Timestamp - history.Samples[0].Timestamp);
        history.Add(Sample(start.AddSeconds(184)));
        Assert.Equal(start.AddSeconds(4), history.Samples[0].Timestamp);
    }

    [Fact]
    public void ReplayReplacesWindowWhenScrubbingBackward()
    {
        var history = new TelemetryHistory();
        var start = DateTimeOffset.UtcNow;
        history.Add(Sample(start.AddMinutes(5)));
        history.Replace([Sample(start), Sample(start.AddSeconds(2))]);
        Assert.Equal(2, history.Samples.Count);
        Assert.Equal(start, history.Samples[0].Timestamp);
    }

    [Fact]
    public void BurstDoesNotBecomeSustainedFinding()
    {
        var start = DateTimeOffset.UtcNow;
        var samples = Enumerable.Range(0, 31).Select(index => Sample(start.AddSeconds(index * 2), index < 6 ? 99 : 10)).ToArray();
        Assert.Empty(WindowDiagnosticAnalyzer.Analyze(samples));
    }

    [Fact]
    public void SustainedLoadIsDetectedButSamplingGapBreaksRun()
    {
        var start = DateTimeOffset.UtcNow;
        var samples = Enumerable.Range(0, 16).Select(index => Sample(start.AddSeconds(index * 2))).ToArray();
        Assert.Contains(WindowDiagnosticAnalyzer.Analyze(samples), finding => finding.Title == "Sustained CPU saturation");
        var withGap = samples.Take(8).Concat(samples.Skip(8).Select(sample => sample with { Timestamp = sample.Timestamp.AddSeconds(20) })).ToArray();
        Assert.Empty(WindowDiagnosticAnalyzer.Analyze(withGap));
    }

    [Fact]
    public void MissingCpuSamplesBreakSustainedCondition()
    {
        var start = DateTimeOffset.UtcNow;
        var samples = Enumerable.Range(0, 25).Select(index => Sample(start.AddSeconds(index * 2))).ToArray();
        samples[12] = samples[12] with { Cpu = samples[12].Cpu with { Availability = MetricAvailability.Unsupported("Missing") } };
        Assert.Empty(WindowDiagnosticAnalyzer.Analyze(samples));
    }

    [Fact]
    public void GenericAndLegacyPowerAreNotAttributedToCpu()
    {
        var snapshot = Sample(DateTimeOffset.UtcNow) with
        {
            Power = new PowerMetric(PowerSource.AC, null, null, MetricAvailability.Supported, CpuPackageWatts: 90),
            Sensors = [new SensorMetric("meter0", "0", "Power", "Power", "W", 100, "Windows Power Meter", "Unidentified hardware domain")],
            GpuDevices = [new GpuDeviceMetric("GPU", "NVML", 40, 30, null, null)]
        };
        var sensors = SensorCatalog.GetSensors(snapshot);
        Assert.Null(Assert.Single(sensors, sensor => sensor.Scope == "CPU package").Value);
        Assert.Equal(90d, Assert.Single(sensors, sensor => sensor.Id == "legacy/power").Value);
        Assert.Equal(30d, Assert.Single(sensors, sensor => sensor.Scope == "GPU board").Value);
    }

    [Fact]
    public void IdentifiedPackageSensorsRemainSeparateAcrossSockets()
    {
        var snapshot = Sample(DateTimeOffset.UtcNow) with { Sensors = [
            new SensorMetric("provider/cpu0", "CPU0", "CPU Package", "Power", "W", 45, "Provider", "CPU package"),
            new SensorMetric("provider/cpu1", "CPU1", "CPU Package", "Power", "W", 70, "Provider", "CPU package") ] };
        var packages = SensorCatalog.GetSensors(snapshot).Where(sensor => sensor.Scope == "CPU package").ToArray();
        Assert.Equal(2, packages.Length);
        Assert.Equal(45d, packages[0].Value);
        Assert.Equal(70d, packages[1].Value);
    }

    [Fact]
    public void SingleLogicalProcessorCanSaturateWhileTotalIsLow()
    {
        var start = DateTimeOffset.UtcNow;
        var samples = Enumerable.Range(0, 16).Select(index => Sample(start.AddSeconds(index * 2), 12) with {
            Cpu = new CpuMetric(12, 8, MetricAvailability.Supported) { LogicalProcessors = [new LogicalProcessorMetric("0,3", 99, 3200)] }
        }).ToArray();
        Assert.Contains(WindowDiagnosticAnalyzer.Analyze(samples), finding => finding.Title.Contains("(0,3)", StringComparison.Ordinal));
        Assert.DoesNotContain(WindowDiagnosticAnalyzer.Analyze(samples), finding => finding.Title == "Sustained CPU saturation");
    }
}
