using Skald.Core.Models;
using Xunit;

namespace Skald.Core.Tests;

public sealed class ProcessMetricTests
{
    [Fact]
    public void FormatsAvailableProcessCounters()
    {
        var metric = new ProcessMetric(42, DateTimeOffset.UtcNow, "Example", null, 2.5, 100, 80)
        {
            ReadBytesPerSecond = 2048,
            WriteBytesPerSecond = 0,
            ThreadCount = 12,
            HandleCount = 34,
            PeakWorkingSetMegabytes = 120,
            TotalCpuTime = TimeSpan.FromHours(2) + TimeSpan.FromMinutes(3),
            PageFaultCount = 345
        };

        Assert.Equal("2.5%", metric.CpuTableDisplay);
        Assert.Equal("2.0 KB/s", metric.ReadDisplay);
        Assert.Equal("0.0 B/s", metric.WriteDisplay);
        Assert.Equal("02:03:00", metric.CpuTimeDisplay);
    }

    [Fact]
    public void MissingCountersAreNotShownAsZero()
    {
        var metric = new ProcessMetric(42, null, "Restricted", null, 0, 0, 0)
        {
            CpuAvailable = false,
            WorkingSetAvailable = false,
            PrivateMemoryAvailable = false
        };

        Assert.Equal("—", metric.CpuTableDisplay);
        Assert.Equal("—", metric.WorkingSetDisplay);
        Assert.Equal("—", metric.PrivateMemoryDisplay);
        Assert.Equal("—", metric.ReadDisplay);
        Assert.Equal("—", metric.ThreadDisplay);
        Assert.Equal("—", metric.PageFaultDisplay);
    }
}
