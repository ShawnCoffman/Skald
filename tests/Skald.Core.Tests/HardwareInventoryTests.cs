using Skald.Core.Models;
using Xunit;

namespace Skald.Core.Tests;

public sealed class HardwareInventoryTests
{
    [Theory]
    [InlineData(0u, "0 C:", true)]
    [InlineData(1u, "1 D:", true)]
    [InlineData(1u, "10 D:", false)]
    [InlineData(1u, "_Total", false)]
    public void DiskCounterMatchRequiresWholeNumberPrefix(uint number, string instance, bool expected)
        => Assert.Equal(expected, HardwareIdentity.MatchesDiskCounter(number, instance));

    [Fact]
    public void DeviceHealthUsesWorstReportedEvidence()
    {
        var device = new HardwareDevice("disk/1", "Storage", "Disk", "ID", HardwareMatchConfidence.Verified, [],
        [
            new("Provider healthy", "Reported healthy", HardwareHealthState.NoProblemObserved, "provider"),
            new("Errors", "Uncorrected errors increased", HardwareHealthState.Attention, "counter")
        ]);
        Assert.Equal(HardwareHealthState.Attention, device.Health);
    }

    [Fact]
    public void UnknownDoesNotBecomeHealthy()
    {
        var device = new HardwareDevice("memory/1", "Memory", "Slot 1", "Location", HardwareMatchConfidence.Verified, [],
            [new("Unmeasured", "No per-slot error evidence", HardwareHealthState.Unknown, "inventory")]);
        Assert.Equal(HardwareHealthState.Unknown, device.Health);
        Assert.Equal("Unknown", device.HealthLabel);
    }
}
