using Skald.Collectors;
using Skald.Core.Models;
using Xunit;
using Xunit.Abstractions;

namespace Skald.Core.Tests;

public sealed class SystemInventoryTests(ITestOutputHelper output)
{
    [Fact]
    public void QueuedFileOperationsDoNotImplyServicingOrUpdateRestart()
    {
        var indicators = new RestartIndicators(false, false, 2);
        Assert.False(indicators.RestartRequested);
        Assert.Contains("2 file operations queued for next boot", indicators.Status);
        Assert.True(new RestartIndicators(true, false, 2).RestartRequested);
        Assert.True(new RestartIndicators(false, true, 0).RestartRequested);
    }

    [Theory]
    [InlineData(22u, true)]
    [InlineData(28u, false)]
    [InlineData(43u, false)]
    public void DisabledDevicesAreDistinctFromFaults(uint code, bool disabled)
    {
        Assert.Equal(disabled, new DeviceProblem("Device", code, "test").IsDisabled);
    }

    [Fact]
    public async Task CollectsMachineInventoryWithoutBlockingTelemetry()
    {
        var inventoryTask = SystemInventoryCollector.CollectAsync();
        using var telemetry = new WindowsMetricsCollector();
        var sample = await telemetry.SampleAsync();
        var inventory = await inventoryTask.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(sample.Memory.Availability.IsSupported);
        Assert.Contains(inventory.Machine, detail => detail.Label == "System name" && detail.Value == Environment.MachineName);
        Assert.Contains(inventory.OperatingSystem, detail => detail.Label == "Operating system");
        if (inventory.OperatingSystem.Any(detail => detail.Label == "Operating system" && detail.Value == "Unknown"))
            Assert.Contains(inventory.Limitations, limitation => limitation.StartsWith("Operating system:", StringComparison.Ordinal));
        Assert.NotEmpty(inventory.Applications);
        Assert.All(inventory.DeviceProblems, device => Assert.NotEqual(0u, device.Code));
        Assert.All(inventory.Volumes, volume => Assert.InRange(volume.FreePercent, 0, 100));
        output.WriteLine($"Apps: {inventory.Applications.Count}; problems: {inventory.DeviceProblems.Count}; volumes: {inventory.Volumes.Count}");
        output.WriteLine(string.Join("\n", inventory.Limitations));
    }
}
