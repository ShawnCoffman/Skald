using Skald.Collectors;
using Skald.Core.Models;
using Skald.Diagnostics;
using Skald.Triage;
using Xunit;

namespace Skald.Core.Tests;

public sealed class PowerTelemetryTests
{
    private static SystemMetricsSnapshot Sample(DateTimeOffset time) => new(time,
        new CpuMetric(20, 8, MetricAvailability.Supported), new MemoryMetric(16_000_000_000, 8_000_000_000, 50, MetricAvailability.Supported),
        new DiskMetric(0, 0, MetricAvailability.Supported), []);

    [Theory]
    [InlineData("RAPL_Package0_PKG", "CPU 0", "CPU package")]
    [InlineData("RAPL_Package1_PKG", "CPU 1", "CPU package")]
    [InlineData("RAPL_Package0_PP0", "CPU 0", "CPU cores")]
    [InlineData("RAPL_Package0_PP1", "CPU 0", "CPU integrated graphics")]
    [InlineData("rapl_package0_dram", "CPU 0", "DRAM")]
    [InlineData("RAPL_Package0_PSYS", "CPU 0", "Platform")]
    [InlineData("RAPL_Package0_XYZ", "CPU 0", "Unidentified hardware domain")]
    [InlineData("Vendor channel", "Vendor channel", "Unidentified hardware domain")]
    public void EnergyMeterDomainsMapToSeparateScopes(string instance, string device, string scope)
    {
        var description = WindowsPowerTelemetryCollector.Describe(instance);
        Assert.Equal(device, description.Device);
        Assert.Equal(scope, description.Scope);
    }

    [Fact]
    public void EnergyMeterPackageIsTheCpuPackageReading()
    {
        var snapshot = Sample(DateTimeOffset.UtcNow) with { Sensors = [
            new SensorMetric("windows/energy-meter/RAPL_Package0_PKG", "CPU 0", "CPU package power", "Power", "W", 61.5, "Windows Energy Meter (RAPL)", "CPU package"),
            new SensorMetric("windows/energy-meter/RAPL_Package0_PP0", "CPU 0", "CPU cores power", "Power", "W", 48, "Windows Energy Meter (RAPL)", "CPU cores") ] };
        var sensors = SensorCatalog.GetSensors(snapshot);
        Assert.Equal(61.5, Assert.Single(sensors, sensor => sensor.Scope == "CPU package").Value);
        Assert.DoesNotContain(sensors, sensor => sensor.Id == "cpu/package/unavailable");
    }

    [Fact]
    public void GpuLimitSensorsDoNotCountAsBoardPower()
    {
        var snapshot = Sample(DateTimeOffset.UtcNow) with { GpuDevices = [new GpuDeviceMetric("GPU", "NVIDIA NVML", 60, 200, null, null)
            { DeviceId = "gpu-1", PowerLimitWatts = 320, PerformanceState = 0, FanSpeedPercent = 45, ClockLimitReasons = 0x4 }] };
        var sensors = SensorCatalog.GetSensors(snapshot);
        Assert.Equal(200d, Assert.Single(sensors, sensor => sensor.Scope == "GPU board").Value);
        Assert.Equal(320d, Assert.Single(sensors, sensor => sensor.Scope == "GPU power limit").Value);
        Assert.DoesNotContain(sensors, sensor => sensor.Kind == "Power" && sensor.Value == 320);
    }

    [Fact]
    public void GpuCapReasonsIgnoreIdleAndDecodePowerAndThermal()
    {
        Assert.Empty(SensorCatalog.DescribeGpuCapReasons(0x1));
        Assert.Empty(SensorCatalog.DescribeGpuCapReasons(null));
        Assert.Equal(["power cap", "hardware thermal"], SensorCatalog.DescribeGpuCapReasons(0x4 | 0x40 | 0x1));
    }

    [Fact]
    public void SustainedCpuPerformanceLimitIsAPlatformFinding()
    {
        var start = DateTimeOffset.UtcNow;
        SensorMetric Limit(double value) => new("windows/cpu/performance-limit", "CPU", "Performance limit", "Limit", "%", value, "test", "CPU performance limit");
        var limited = Enumerable.Range(0, 20).Select(index => Sample(start.AddSeconds(index * 2)) with { Sensors = [Limit(80)] }).ToArray();
        var finding = Assert.Single(WindowDiagnosticAnalyzer.Analyze(limited), item => item.Title == "CPU performance limit");
        Assert.True(Handoff.IsPlatformFinding(finding.Title));
        var unlimited = limited.Select(sample => sample with { Sensors = [Limit(100)] }).ToArray();
        Assert.DoesNotContain(WindowDiagnosticAnalyzer.Analyze(unlimited), item => item.Title == "CPU performance limit");
    }

    [Fact]
    public void GpuThermalSlowdownIsSeparatedFromNormalPowerCap()
    {
        var start = DateTimeOffset.UtcNow;
        SystemMetricsSnapshot[] Run(ulong reasons) => Enumerable.Range(0, 20).Select(index => Sample(start.AddSeconds(index * 2)) with
            { GpuDevices = [new GpuDeviceMetric("GPU", "NVIDIA NVML", 85, 300, null, null) { ClockLimitReasons = reasons }] }).ToArray();
        var powerCapped = WindowDiagnosticAnalyzer.Analyze(Run(0x4));
        var capFinding = Assert.Single(powerCapped, item => item.Title == "GPU power cap · GPU");
        Assert.False(Handoff.IsPlatformFinding(capFinding.Title));
        Assert.DoesNotContain(powerCapped, item => item.Title.StartsWith("GPU thermal", StringComparison.Ordinal));
        var thermal = Assert.Single(WindowDiagnosticAnalyzer.Analyze(Run(0x40)), item => item.Title == "GPU thermal or hardware slowdown · GPU");
        Assert.True(Handoff.IsPlatformFinding(thermal.Title));
        Assert.Empty(WindowDiagnosticAnalyzer.Analyze(Run(0x1)));
    }
}
