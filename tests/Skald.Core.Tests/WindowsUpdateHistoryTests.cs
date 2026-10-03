using Skald.Core.Models;
using Xunit;

namespace Skald.Core.Tests;

public sealed class WindowsUpdateHistoryTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);

    private static WindowsUpdateEntry Entry(double daysAgo, string title, string result = "Succeeded", string code = "0x00000000")
        => new(Now.AddDays(-daysAgo), title, "Install", result, code);

    private static WindowsUpdateHistory History(WindowsUpdateEntry[] entries, int? total = null, bool limit = false, DateTimeOffset? since = null)
        => new(Now, entries.OrderByDescending(entry => entry.Date).ToArray(), null, null, []) { TotalHistoryCount = total ?? entries.Length, ReachedReadLimit = limit, RequestedSince = since };

    [Fact]
    public void RepeatedFailuresBecomeOneRowWithCountAndLatestCode()
    {
        var history = History([
            Entry(0.1, "2026-09 Cumulative Update (KB5050001)", "Failed", "0x80070002"),
            Entry(0.5, "2026-09 Cumulative Update (KB5050001)", "Failed", "0x800F0922"),
            Entry(1.5, "2026-09 Cumulative Update (KB5050001)", "Failed", "0x800F0922"),
            Entry(0.2, "Security Intelligence Update for Microsoft Defender Antivirus - KB2267602 (Version 1.419.1.0)"),
            Entry(0.7, "Security Intelligence Update for Microsoft Defender Antivirus - KB2267602 (Version 1.419.0.0)")]);
        var rows = WindowsUpdateHistoryView.Rows(history, Now.AddDays(-7), failuresOnly: false);
        var failed = Assert.Single(rows, row => row.IsFailure);
        Assert.Equal(3, failed.Count);
        Assert.Equal("0x80070002", failed.LatestHResult);
        Assert.Contains("×3", failed.Detail, StringComparison.Ordinal);
        // Defender definition updates are shown, one row per version.
        Assert.Equal(2, rows.Count(row => row.Title.StartsWith("Security Intelligence Update", StringComparison.Ordinal)));
        Assert.Equal(failed, Assert.Single(WindowsUpdateHistoryView.Rows(history, Now.AddDays(-7), failuresOnly: true)));
    }

    [Fact]
    public void RangeFiltersByDate()
    {
        var history = History([Entry(0.5, "Today"), Entry(2, "Two days ago"), Entry(10, "Ten days ago")]);
        Assert.Equal(["Today"], WindowsUpdateHistoryView.Rows(history, Now.AddDays(-1), false).Select(row => row.Title));
        Assert.Equal(3, WindowsUpdateHistoryView.Rows(history, null, false).Count);
    }

    [Fact]
    public void CoverageSaysWhenTheReadLimitOrWindowsHistoryFallsShortOfTheRange()
    {
        var recent = new[] { Entry(1, "A"), Entry(2, "B") };
        Assert.Null(WindowsUpdateHistoryView.Coverage(History([.. recent, Entry(20, "Old")]), Now.AddDays(-14)));
        Assert.Contains("Only the newest 2 entries were read", WindowsUpdateHistoryView.Coverage(History(recent, total: 500, limit: true), Now.AddDays(-14)), StringComparison.Ordinal);
        Assert.Contains("history begins", WindowsUpdateHistoryView.Coverage(History(recent), Now.AddDays(-14)), StringComparison.Ordinal);
        Assert.Null(WindowsUpdateHistoryView.Coverage(History(recent), Now.AddDays(-1.5)));
    }
}
