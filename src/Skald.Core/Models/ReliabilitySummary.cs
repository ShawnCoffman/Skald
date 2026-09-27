namespace Skald.Core.Models;

public sealed record ReliabilitySummary(int AppCrashes, int AppHangs, int UnexpectedShutdowns,
    int BugChecks, int HardwareReports, ReliabilityEvent? LatestProblem)
{
    public static ReliabilitySummary From(ReliabilityHistory history, TimeSpan window)
    {
        var cutoff = history.CollectedAt - window;
        var reports = history.Events.Where(item => item.Timestamp >= cutoff && item.Timestamp <= history.CollectedAt).ToArray();
        var appCrashes = reports.Count(item => item.Provider.Equals("Application Error", StringComparison.OrdinalIgnoreCase) && item.EventId == 1000);
        var hangs = reports.Count(item => item.Provider.Equals("Application Hang", StringComparison.OrdinalIgnoreCase) && item.EventId == 1002);
        var shutdowns = reports.Where(item => item.Provider.Equals("Microsoft-Windows-Kernel-Power", StringComparison.OrdinalIgnoreCase) && item.EventId == 41
            || item.Provider.Equals("EventLog", StringComparison.OrdinalIgnoreCase) && item.EventId == 6008)
            .OrderBy(item => item.Timestamp).Aggregate(new List<ReliabilityEvent>(), (groups, item) =>
            {
                if (groups.Count == 0 || item.Timestamp - groups[^1].Timestamp > TimeSpan.FromMinutes(10)) groups.Add(item);
                return groups;
            }).Count;
        var bugChecks = reports.Count(item => (item.Provider.Equals("BugCheck", StringComparison.OrdinalIgnoreCase)
            || item.Provider.Contains("WER-SystemErrorReporting", StringComparison.OrdinalIgnoreCase)) && item.EventId == 1001);
        var hardware = reports.Count(item => item.Category == "Hardware");
        var latest = reports.FirstOrDefault(item => item.Category != "Changes" && (item.Severity is "Critical" or "Error"));
        return new(appCrashes, hangs, shutdowns, bugChecks, hardware, latest);
    }
}
