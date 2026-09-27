using Skald.Core.Models;
using Xunit;

namespace Skald.Core.Tests;

public sealed class ProcessGroupingTests
{
    [Fact]
    public void GroupsRelatedProcessesByExecutableAndSumsAvailableCounters()
    {
        var groups = ProcessGrouping.Create(
        [
            new ProcessMetric(10, null, "ChatGPT", @"C:\Apps\ChatGPT.exe", 1, 100, 80)
            {
                HasVisibleWindow = true,
                ReadBytesPerSecond = 1024,
                ThreadCount = 10
            },
            new ProcessMetric(11, null, "ChatGPT", @"c:\apps\CHATGPT.exe", 2, 200, 150)
            {
                ReadBytesPerSecond = 2048,
                ThreadCount = 20
            },
            new ProcessMetric(12, null, "ChatGPT", @"D:\Other\ChatGPT.exe", 3, 300, 250)
        ]);

        Assert.Equal(2, groups.Count);
        var app = Assert.Single(groups, group => group.IsApplication);
        Assert.Equal(2, app.Processes.Count);
        Assert.Equal(3, app.CpuPercent);
        Assert.Equal(300, app.WorkingSetMegabytes);
        Assert.Equal(3072, app.ReadBytesPerSecond);
        Assert.Equal(30, app.ThreadCount);
        Assert.Equal(10, app.PrimaryProcessId);
    }

    [Fact]
    public void GroupsUnknownPathsByNameWithoutInventingAnAppWindow()
    {
        var groups = ProcessGrouping.Create(
        [
            new ProcessMetric(1, null, "restricted", null, 0, 0, 0),
            new ProcessMetric(2, null, "Restricted", null, 0, 0, 0)
        ]);

        var group = Assert.Single(groups);
        Assert.Equal(2, group.Processes.Count);
        Assert.False(group.IsApplication);
    }

    [Fact]
    public void UsesBusiestSharedGpuEngineForAppGroup()
    {
        var group = Assert.Single(ProcessGrouping.Create(
        [
            new ProcessMetric(1, null, "Browser", @"C:\Browser.exe", 0, 0, 0)
            {
                GpuEngines = new Dictionary<string, double> { ["gpu0-3d"] = 20, ["gpu0-copy"] = 35 },
                GpuDedicatedMegabytes = 200,
                GpuSharedMegabytes = 50
            },
            new ProcessMetric(2, null, "Browser", @"C:\Browser.exe", 0, 0, 0)
            {
                GpuEngines = new Dictionary<string, double> { ["gpu0-3d"] = 25, ["gpu0-copy"] = 10 },
                GpuDedicatedMegabytes = 300,
                GpuSharedMegabytes = 75
            }
        ]));

        Assert.Equal(35, group.Processes[0].GpuPercent);
        Assert.Equal(45, group.GpuPercent);
        Assert.Equal(500, group.GpuDedicatedMegabytes);
        Assert.Equal(125, group.GpuSharedMegabytes);
    }

    [Fact]
    public void DistinguishesUnavailableGpuFromMeasuredIdle()
    {
        var unavailable = new ProcessMetric(1, null, "Unknown", null, 0, 0, 0);
        var idle = new ProcessMetric(2, null, "Idle", null, 0, 0, 0)
        {
            GpuEngines = new Dictionary<string, double>()
        };

        Assert.Null(unavailable.GpuPercent);
        Assert.Equal("—", unavailable.GpuDisplay);
        Assert.Equal(0, idle.GpuPercent);
        Assert.Equal("0.0%", idle.GpuDisplay);
    }
}
