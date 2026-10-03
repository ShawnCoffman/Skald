using Skald.Core.Models;

namespace Skald.Triage;

public static class WindowsUpdateCollector
{
    // Defender definition updates arrive several times a day, so a 30-day window can hold hundreds of entries.
    public const int ReadLimit = 2000;
    private const int PageSize = 100;

    // Reads history newest first until an entry older than since (null: all of it), the start of history, or ReadLimit.
    public static Task<WindowsUpdateHistory> CollectAsync(DateTimeOffset? since = null, int maxEntries = ReadLimit)
        => Task.Run(() => Collect(since, maxEntries));

    private static WindowsUpdateHistory Collect(DateTimeOffset? since, int maxEntries)
    {
        var entries = new List<WindowsUpdateEntry>();
        var status = new List<string>();
        int? pending = null;
        int? total = null;
        DateTimeOffset? lastSearch = null;
        try
        {
            var sessionType = Type.GetTypeFromProgID("Microsoft.Update.Session")
                ?? throw new InvalidOperationException("Windows Update Agent is unavailable.");
            dynamic session = Activator.CreateInstance(sessionType)!;
            dynamic searcher = session.CreateUpdateSearcher();
            total = (int)searcher.GetTotalHistoryCount();
            var limit = Math.Min(maxEntries, total.Value);
            // A whole page is kept even past the cutoff, so the oldest entry read shows the requested range was covered.
            for (var start = 0; start < limit && (since is null || entries.Count == 0 || entries[^1].Date >= since); start += PageSize)
            {
                dynamic history = searcher.QueryHistory(start, Math.Min(PageSize, limit - start));
                var count = (int)history.Count;
                if (count == 0) break;
                for (var i = 0; i < count; i++)
                {
                    dynamic entry = history.Item(i);
                    var operation = (int)entry.Operation switch { 1 => "Install", 2 => "Uninstall", _ => "Other" };
                    var result = (int)entry.ResultCode switch { 2 => "Succeeded", 3 => "Succeeded with errors", 4 => "Failed", 5 => "Aborted", _ => "In progress / unknown" };
                    entries.Add(new(DateTime.SpecifyKind((DateTime)entry.Date, DateTimeKind.Utc), (string)entry.Title, operation, result, $"0x{(uint)(int)entry.HResult:X8}"));
                }
            }
            if (entries.Count >= maxEntries && entries.Count < total) status.Add($"Windows Update history: read limit of {maxEntries} entries reached");
            try
            {
                searcher.Online = false;
                dynamic result = searcher.Search("IsInstalled=0 and IsHidden=0 and Type='Software'");
                pending = (int)result.Updates.Count;
                status.Add("Pending count uses the local Windows Update catalog; it may be stale until Windows checks for updates.");
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            { status.Add($"Pending updates: unavailable ({ex.GetType().Name})"); }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or UnauthorizedAccessException)
        { status.Add($"Windows Update history: unavailable ({ex.GetType().Name})"); }
        try
        {
            var autoType = Type.GetTypeFromProgID("Microsoft.Update.AutoUpdate");
            if (autoType is not null)
            {
                dynamic auto = Activator.CreateInstance(autoType)!;
                var date = (DateTime)auto.Results.LastSearchSuccessDate;
                // Windows Update Agent dates are UTC, like the history entries above.
                if (date.Year > 2000) lastSearch = DateTime.SpecifyKind(date, DateTimeKind.Utc);
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        { status.Add($"Last update check: unavailable ({ex.GetType().Name})"); }
        var readLimit = entries.Count >= maxEntries && total > entries.Count && (since is null || entries.Min(item => item.Date) > since);
        return new(DateTimeOffset.Now, entries.OrderByDescending(item => item.Date).ToArray(), pending, lastSearch, status)
            { TotalHistoryCount = total, RequestedSince = since, ReachedReadLimit = readLimit };
    }
}
