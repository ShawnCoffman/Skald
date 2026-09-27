using System.Diagnostics;
using Skald.Collectors;
using Xunit;

namespace Skald.Core.Tests;

public sealed class GpuMemoryCollectorTests
{
    [Theory]
    [InlineData("pid_10432_luid_0x00000000_0x00012AB6_phys_0", 10432)]
    [InlineData("PID_4_LUID_0x00000000_0x00012AB6_phys_0", 4)]
    public void ParsesGpuProcessMemoryInstance(string instance, int expectedProcessId)
    {
        Assert.True(GpuMemoryCollector.TryParseProcessInstance(instance, out var processId));
        Assert.Equal(expectedProcessId, processId);
    }

    [Fact]
    public void RejectsNonProcessMemoryInstance()
        => Assert.False(GpuMemoryCollector.TryParseProcessInstance("luid_0x00000000_0x00012AB6_phys_0", out _));

    [Fact]
    public void ReadsGpuMemoryWhenCountersArePresent()
    {
        if (!OperatingSystem.IsWindows() || !PerformanceCounterCategory.Exists("GPU Adapter Memory")) return;
        using var collector = new GpuMemoryCollector();
        var sample = collector.Sample();
        Assert.NotNull(sample);
        Assert.All(sample.Adapters, adapter => Assert.True(adapter.DedicatedUsedMegabytes is >= 0));
    }
}
