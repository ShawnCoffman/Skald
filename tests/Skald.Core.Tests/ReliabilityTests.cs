using Skald.Core.Models;
using Xunit;

namespace Skald.Core.Tests;

public sealed class ReliabilityTests
{
    [Fact]
    public void SeparatesErrorReportingSignatures()
    {
        const string raw = "<Event><EventData><Data Name='EventName'>LiveKernelEvent</Data><Data Name='P1'>1b8</Data></EventData></Event>";
        Assert.Equal("LiveKernelEvent · 1b8", Skald.Triage.WindowsReliabilityCollector.Component(raw, "Windows Error Reporting"));
        Assert.NotEqual(Skald.Triage.WindowsReliabilityCollector.Component(raw, "Windows Error Reporting"),
            Skald.Triage.WindowsReliabilityCollector.Component(raw.Replace("1b8", "141", StringComparison.Ordinal), "Windows Error Reporting"));
    }
    [Theory]
    [InlineData("Microsoft-Windows-WHEA-Logger", 19, "Hardware")]
    [InlineData("Microsoft-Windows-Kernel-Power", 41, "Crashes & restarts")]
    [InlineData("EventLog", 6008, "Crashes & restarts")]
    [InlineData("Application Error", 1000, "Applications")]
    [InlineData("Windows Error Reporting", 1001, "Windows reports")]
    [InlineData("Display", 4101, "Drivers & storage")]
    [InlineData("Microsoft-Windows-WindowsUpdateClient", 19, "Changes")]
    [InlineData("Unrelated provider", 41, null)]
    public void ClassifiesUsingProviderAndEventTogether(string provider, int id, string? category)
        => Assert.Equal(category, ReliabilityAnalysis.Category(provider, id));

    [Fact]
    public void DeduplicatesCopiesWithoutMergingDistinctReports()
    {
        var now = DateTimeOffset.UtcNow;
        var first = Event(now, 123);
        var duplicate = first with { Log = "system", Details = "WMI copy" };
        var otherRecord = Event(now, 124);
        var recycledId = Event(now.AddDays(-1), 123);
        var result = ReliabilityAnalysis.Deduplicate([first, duplicate, otherRecord, recycledId]);
        Assert.Equal(3, result.Count);
        Assert.Equal("native", result[0].Details);
        Assert.Equal(now.AddDays(-1), result[^1].Timestamp);
    }

    [Fact]
    public void UnexpectedShutdownDoesNotClaimHardwareCause()
    {
        var item = Event(DateTimeOffset.UtcNow, 1);
        Assert.Contains("does not identify", ReliabilityAnalysis.Interpretation(item), StringComparison.Ordinal);
        Assert.Contains("next boot", ReliabilityAnalysis.Interpretation(item), StringComparison.Ordinal);
    }

    [Fact]
    public void SummarySeparatesCrashesFromDumpsAndGroupsShutdownReports()
    {
        var now = DateTimeOffset.UtcNow;
        var reports = new[]
        {
            new ReliabilityEvent(now.AddHours(-1), "Application", 1, "Application Error", 1000, "Applications", "Error", "app.exe", "Crash", "", ""),
            new ReliabilityEvent(now.AddHours(-1).AddSeconds(1), "Application", 2, "Windows Error Reporting", 1001, "Windows reports", "Information", "AppCrash · app.exe", "Report", "", ""),
            new ReliabilityEvent(now.AddHours(-2), "System", 3, "Microsoft-Windows-Kernel-Power", 41, "Crashes & restarts", "Critical", "System", "Unclean shutdown", "", ""),
            new ReliabilityEvent(now.AddHours(-2).AddMinutes(1), "System", 4, "EventLog", 6008, "Crashes & restarts", "Error", "System", "Unexpected shutdown", "", ""),
            new ReliabilityEvent(now.AddDays(-8), "Application", 5, "Application Error", 1000, "Applications", "Error", "old.exe", "Old crash", "", "")
        };
        var summary = ReliabilitySummary.From(new(now, reports, []), TimeSpan.FromDays(7));
        Assert.Equal(1, summary.AppCrashes);
        Assert.Equal(1, summary.UnexpectedShutdowns);
        Assert.Equal(0, summary.BugChecks);
        Assert.Equal("app.exe", summary.LatestProblem?.Component);
    }

    private static ReliabilityEvent Event(DateTimeOffset time, long record) => new(time, "System", record,
        "Microsoft-Windows-Kernel-Power", 41, "Crashes & restarts", "Critical", "System", "Restart", "native", "raw");
}
