using System.Diagnostics;
using Skald.Collectors;
using Xunit;
using Xunit.Abstractions;

namespace Skald.Core.Tests;

public sealed class WindowsMetricsCollectorTests
{
    private readonly ITestOutputHelper _output;

    public WindowsMetricsCollectorTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task SamplesSupportedWindowsTelemetry()
    {
        using var collector = new WindowsMetricsCollector();

        var stopwatch = Stopwatch.StartNew();
        var first = await collector.SampleAsync();
        var firstElapsed = stopwatch.Elapsed;
        await Task.Delay(250);
        stopwatch.Restart();
        var second = await collector.SampleAsync();
        _output.WriteLine($"First sample: {firstElapsed.TotalMilliseconds:F0} ms; steady sample: {stopwatch.Elapsed.TotalMilliseconds:F0} ms; processes: {second.Processes.Count}; GPU adapters: {second.GpuAdapters.Count}; GPU devices: {second.GpuDevices.Count}");
        foreach (var device in second.GpuDevices)
            _output.WriteLine($"{device.Name}: {device.TemperatureCelsius:F0} °C, {device.PowerWatts:F1} W, {device.DedicatedUsedMegabytes:F0}/{device.DedicatedTotalMegabytes:F0} MB");

        Assert.True(second.Memory.Availability.IsSupported);
        Assert.True(second.Memory.TotalBytes > 0);
        Assert.True(second.Memory.CommitLimitBytes > 0);
        Assert.True(second.Memory.CommitBytes <= second.Memory.CommitLimitBytes);
        Assert.True(second.Processes.Count > 0);
        Assert.True(second.Cpu.UtilizationPercent is >= 0 and <= 100);
        Assert.True(second.Power.Availability.IsSupported);
        Assert.True(second.Network.Availability.IsSupported);
        Assert.Equal(second.Network.ActiveInterfaceCount, second.Network.Interfaces.Count);
        Assert.True(first.Timestamp < second.Timestamp);
        var self = Assert.Single(second.Processes, process => process.ProcessId == Environment.ProcessId);
        Assert.True(self.ThreadCount > 0);
        Assert.True(self.HandleCount > 0);
        Assert.True(self.PeakWorkingSetMegabytes > 0);
        Assert.NotNull(self.PageFaultCount);
        Assert.All(second.GpuAdapters, adapter => Assert.True(adapter.DedicatedUsedMegabytes is >= 0));
        Assert.All(second.GpuDevices, device => Assert.True(device.TemperatureCelsius is >= 0 or null));
    }
}
