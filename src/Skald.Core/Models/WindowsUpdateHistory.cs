using System.Globalization;

namespace Skald.Core.Models;

public sealed record WindowsUpdateEntry(DateTimeOffset Date, string Title, string Operation, string Result, string? HResult)
{
    public bool IsFailure => Result is "Failed" or "Aborted" or "Succeeded with errors";
}

public sealed record WindowsUpdateHistory(DateTimeOffset CollectedAt, IReadOnlyList<WindowsUpdateEntry> Entries,
    int? PendingCount, DateTimeOffset? LastSuccessfulSearch, IReadOnlyList<string> SourceStatus)
{
    // Total entries Windows holds; more than Entries.Count means only the newest part of the history was read.
    public int? TotalHistoryCount { get; init; }
    // The date the history was read back to; null means all of it (up to the read limit).
    public DateTimeOffset? RequestedSince { get; init; }
    // True when the read limit stopped reading before RequestedSince (or the start of history) was reached.
    public bool ReachedReadLimit { get; init; }
}

// One line per update and outcome: the same failed update retrying six times is one row with ×6 and its latest error code.
public sealed record WindowsUpdateRow(string Title, string Operation, string Result, int Count, DateTimeOffset Latest, DateTimeOffset First, string? LatestHResult)
{
    public bool IsFailure => Result is "Failed" or "Aborted" or "Succeeded with errors";

    public string Detail => FormattableString.Invariant($"{Latest.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)} · {Operation} · {Result}")
        + (Count > 1 ? FormattableString.Invariant($" ×{Count} (first {First.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)})") : string.Empty)
        + (IsFailure && LatestHResult is { } code ? $" · {code}" : string.Empty);
}

public static class WindowsUpdateHistoryView
{
    public static IReadOnlyList<WindowsUpdateRow> Rows(WindowsUpdateHistory history, DateTimeOffset? since, bool failuresOnly)
    {
        ArgumentNullException.ThrowIfNull(history);
        return history.Entries
            .Where(entry => (since is null || entry.Date >= since) && (!failuresOnly || entry.IsFailure))
            .GroupBy(entry => (entry.Title, entry.Operation, entry.Result))
            .Select(group =>
            {
                var latest = group.MaxBy(entry => entry.Date)!;
                return new WindowsUpdateRow(group.Key.Title, group.Key.Operation, group.Key.Result, group.Count(), latest.Date, group.Min(entry => entry.Date), latest.HResult);
            })
            .OrderByDescending(row => row.Latest).ToArray();
    }

    // Says how far back the history actually goes when it does not cover the requested range, so an empty list is not read as "nothing changed".
    public static string? Coverage(WindowsUpdateHistory history, DateTimeOffset? since)
    {
        ArgumentNullException.ThrowIfNull(history);
        if (history.Entries.Count == 0) return null;
        var oldest = history.Entries.Min(entry => entry.Date);
        var reachedBack = oldest.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
        if (history.ReachedReadLimit && (since is null || oldest > since))
            return $"Only the newest {history.Entries.Count} entries were read; they reach back to {reachedBack}. Older history was not read.";
        if (since is not null && oldest > since && history.TotalHistoryCount is { } total && total <= history.Entries.Count)
            return $"Windows' update history begins {reachedBack}; nothing older is kept on this machine.";
        return null;
    }
}
