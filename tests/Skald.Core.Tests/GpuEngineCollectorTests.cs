using System.Diagnostics;
using System.Globalization;
using Skald.Collectors;
using Xunit;
using Xunit.Abstractions;

namespace Skald.Core.Tests;

public sealed class GpuEngineCollectorTests
{
    private readonly ITestOutputHelper _output;

    public GpuEngineCollectorTests(ITestOutputHelper output) => _output = output;

    [Theory]
    [InlineData("pid_10432_luid_0x00000000_0x00012AB6_phys_0_eng_0_engtype_3D", 10432, "luid_0x00000000_0x00012AB6_phys_0_eng_0_engtype_3D")]
    [InlineData("PID_4_LUID_0x00000000_0x00012AB6_phys_0_eng_10_engtype_Copy", 4, "LUID_0x00000000_0x00012AB6_phys_0_eng_10_engtype_Copy")]
    public void ParsesWindowsGpuEngineInstance(string instance, int expectedProcessId, string expectedEngine)
    {
        Assert.True(GpuEngineCollector.TryParseInstance(instance, out var processId, out var engine));
        Assert.Equal(expectedProcessId, processId);
        Assert.Equal(expectedEngine, engine);
    }

    [Theory]
    [InlineData("")]
    [InlineData("pid_abc_luid_0x1_phys_0_eng_0")]
    [InlineData("pid_42_engtype_3D")]
    [InlineData("notpid_42_luid_0x1_phys_0_eng_0")]
    public void RejectsNonGpuEngineInstances(string instance)
    {
        Assert.False(GpuEngineCollector.TryParseInstance(instance, out _, out _));
    }

    [Fact]
    public async Task ReadsGpuEngineCountersWhenExposedByWindows()
    {
        if (!OperatingSystem.IsWindows() || !PerformanceCounterCategory.Exists("GPU Engine")) return;
        using var collector = new GpuEngineCollector();
        Assert.Null(collector.Sample()); // Rate counters require a baseline.
        await Task.Delay(500);
        var readings = collector.Sample();
        _output.WriteLine($"GPU processes with activity: {readings?.Count.ToString(CultureInfo.InvariantCulture) ?? "unavailable"}");
        Assert.NotNull(readings);
    }

    [Fact]
    public void AggregatesProcessesOnTheSamePhysicalEngine()
    {
        const string engine = "luid_0x00000000_0x00012AB6_phys_0_eng_0_engtype_3D";
        IReadOnlyDictionary<int, IReadOnlyDictionary<string, double>> readings = new Dictionary<int, IReadOnlyDictionary<string, double>>
        {
            [10] = new Dictionary<string, double> { [engine] = 30 },
            [20] = new Dictionary<string, double> { [engine] = 25 }
        };
        var combined = Assert.Single(GpuEngineCollector.Aggregate(readings));
        Assert.Equal("3D", combined.EngineType);
        Assert.Equal(55, combined.UtilizationPercent);
    }
}
