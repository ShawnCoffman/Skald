namespace Skald.Core.Models;

public sealed record ReliabilityEvent(DateTimeOffset Timestamp, string Log, long RecordId, string Provider,
    int EventId, string Category, string Severity, string Component, string Summary, string Details, string Raw)
{
    public string Identity => $"{Log.ToUpperInvariant()}|{RecordId}|{Timestamp.UtcTicks}";
    public string Signature => $"{Provider}|{EventId}|{Component}";
    public string DisplayTime => Timestamp.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture);
}

public sealed record ReliabilityHistory(DateTimeOffset CollectedAt, IReadOnlyList<ReliabilityEvent> Events, IReadOnlyList<string> SourceStatus);

public static class ReliabilityAnalysis
{
    public static IReadOnlyList<ReliabilityEvent> Deduplicate(IEnumerable<ReliabilityEvent> events) => events
        .DistinctBy(item => item.Identity, StringComparer.OrdinalIgnoreCase).OrderByDescending(item => item.Timestamp).ToArray();

    public static string? Category(string provider, int eventId)
    {
        if (provider.Contains("WHEA", StringComparison.OrdinalIgnoreCase)) return "Hardware";
        if ((provider.Equals("Microsoft-Windows-Kernel-Power", StringComparison.OrdinalIgnoreCase) && eventId == 41)
            || (provider.Equals("EventLog", StringComparison.OrdinalIgnoreCase) && eventId == 6008)
            || provider.Contains("WER-SystemErrorReporting", StringComparison.OrdinalIgnoreCase)
            || provider.Equals("BugCheck", StringComparison.OrdinalIgnoreCase)) return "Crashes & restarts";
        if (provider.Equals("Windows Error Reporting", StringComparison.OrdinalIgnoreCase)) return "Windows reports";
        if (provider.Equals("Application Error", StringComparison.OrdinalIgnoreCase) || provider.Equals("Application Hang", StringComparison.OrdinalIgnoreCase)
            || provider.Equals(".NET Runtime", StringComparison.OrdinalIgnoreCase)) return "Applications";
        if (provider.Equals("Display", StringComparison.OrdinalIgnoreCase) || provider.Contains("nvlddmkm", StringComparison.OrdinalIgnoreCase)
            || provider.Contains("amdkmd", StringComparison.OrdinalIgnoreCase) || provider.Equals("disk", StringComparison.OrdinalIgnoreCase)
            || provider.Contains("storahci", StringComparison.OrdinalIgnoreCase) || provider.Contains("stornvme", StringComparison.OrdinalIgnoreCase)
            || provider.Contains("storport", StringComparison.OrdinalIgnoreCase) || provider.Contains("Ntfs", StringComparison.OrdinalIgnoreCase)
            || provider.Contains("Kernel-PnP", StringComparison.OrdinalIgnoreCase) || provider.Contains("DriverFrameworks", StringComparison.OrdinalIgnoreCase)) return "Drivers & storage";
        if (provider.Equals("MsiInstaller", StringComparison.OrdinalIgnoreCase) || provider.Contains("WindowsUpdateClient", StringComparison.OrdinalIgnoreCase)) return "Changes";
        return null;
    }

    public static string Interpretation(ReliabilityEvent item) => item.Category switch
    {
        "Crashes & restarts" when item.EventId is 41 or 6008 => "Windows recorded an unclean shutdown. This alone does not identify a power-supply, driver, or hardware fault. The timestamp may be when the next boot logged the report; inspect the details for the earlier shutdown time.",
        "Hardware" => "A Windows hardware-error report. Inspect the record for corrected versus uncorrected status and the affected component; severity alone does not establish the failing part.",
        "Changes" => "An installation or update report. A nearby failure is a timing correlation, not proof that this change caused it.",
        _ => "Windows reported this event. Repeated reports can help identify a pattern; the source details remain the evidence. Related events can describe the same underlying incident."
    };
}
