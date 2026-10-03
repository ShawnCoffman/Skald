using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Management;
using System.Xml.Linq;
using Skald.Core.Models;

namespace Skald.Triage;

public static class WindowsReliabilityCollector
{
    // Uncategorized records are skipped before their message is formatted, so a high cap mostly costs enumeration time.
    private const int ScanCap = 20000;

    public static Task<ReliabilityHistory> CollectAsync() => Task.Run(() => Collect(null, null));

    // Only the records written between from and to: a recording saves the reports from its own time window.
    public static Task<ReliabilityHistory> CollectAsync(DateTimeOffset from, DateTimeOffset to) => Task.Run(() => Collect(from, to));

    private static ReliabilityHistory Collect(DateTimeOffset? from, DateTimeOffset? to)
    {
        var now = DateTimeOffset.Now;
        var cutoff = from ?? now.AddDays(-30);
        var end = to ?? now;
        var time = from is null && to is null ? "timediff(@SystemTime) <= 2592000000"
            : $"@SystemTime >= '{Utc(cutoff)}' and @SystemTime <= '{Utc(end)}'";
        var events = new List<ReliabilityEvent>();
        var status = new List<string>();
        var nativeLogs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var truncated = new Dictionary<string, DateTimeOffset>(StringComparer.OrdinalIgnoreCase);
        foreach (var log in new[] { "System", "Application" })
        {
            var read = 0;
            var skipped = 0;
            DateTimeOffset? oldest = null;
            try
            {
                var query = new EventLogQuery(log, PathType.LogName, $"*[System[TimeCreated[{time}] and (Level=1 or Level=2 or Level=3 or Provider[@Name='Microsoft-Windows-WindowsUpdateClient'] or Provider[@Name='MsiInstaller'] or Provider[@Name='Windows Error Reporting'] or Provider[@Name='Microsoft-Windows-WHEA-Logger'] or Provider[@Name='Microsoft-Windows-MemoryDiagnostics-Results'])]]") { ReverseDirection = true };
                using var reader = new EventLogReader(query);
                while (read < ScanCap)
                {
                    using var record = reader.ReadEvent(TimeSpan.FromSeconds(5));
                    if (record is null) break;
                    read++;
                    if (record.TimeCreated is { } seen) oldest = new DateTimeOffset(seen);
                    var provider = record.ProviderName ?? "Unknown";
                    var category = ReliabilityAnalysis.Category(provider, record.Id);
                    if (category is null || record.TimeCreated is not { } created) continue;
                    // One unreadable record is skipped and counted; it must not abort the whole log.
                    try
                    {
                        var raw = record.ToXml();
                        var component = Component(raw, provider);
                        string message;
                        try { message = record.FormatDescription() ?? "No formatted message available. See raw event XML."; }
                        catch (EventLogException) { message = "Message resources unavailable. See raw event XML."; }
                        events.Add(new(new DateTimeOffset(created), log, record.RecordId ?? 0, provider, record.Id, category,
                            record.Level switch { 1 => "Critical", 2 => "Error", 3 => "Warning", _ => "Information" }, component,
                            FirstLine(message, provider, record.Id), message, raw));
                    }
                    catch (Exception ex) when (ex is EventLogException or System.Xml.XmlException or InvalidOperationException) { skipped++; }
                }
                if (skipped > 0) status.Add($"{log}: {skipped} relevant record(s) could not be read and were skipped");
                if (read == ScanCap && oldest is { } first && first > cutoff) truncated[log] = first;
                status.Add($"{log}: {read} records scanned{(read == ScanCap ? $" · limit reached; records before {Stamp(oldest)} were not read" : string.Empty)}");
                nativeLogs.Add(log);
            }
            catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or System.Security.SecurityException)
            { status.Add($"{log}: unavailable / partial ({ex.GetType().Name})"); }
        }
        try
        {
            var date = ManagementDateTimeConverter.ToDmtfDateTime(cutoff.UtcDateTime);
            using var searcher = new ManagementObjectSearcher("root\\CIMV2", $"SELECT TimeGenerated,Logfile,RecordNumber,SourceName,EventIdentifier,ProductName,Message FROM Win32_ReliabilityRecords WHERE TimeGenerated >= '{date}' AND TimeGenerated <= '{ManagementDateTimeConverter.ToDmtfDateTime(end.UtcDateTime)}'", new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(10) });
            using var rows = searcher.Get();
            var read = 0;
            foreach (ManagementBaseObject row in rows)
            {
                using (row)
                {
                    if (++read > 5000) break;
                    if (row["TimeGenerated"] is not string timestamp) continue;
                    if (nativeLogs.Contains(row["Logfile"]?.ToString() ?? string.Empty)) continue;
                    var provider = row["SourceName"]?.ToString() ?? "Unknown";
                    var id = (int)(Convert.ToUInt32(row["EventIdentifier"], CultureInfo.InvariantCulture) & 0xffff);
                    var category = ReliabilityAnalysis.Category(provider, id);
                    if (category is null) continue;
                    var message = row["Message"]?.ToString() ?? "No description available.";
                    events.Add(new(new DateTimeOffset(ManagementDateTimeConverter.ToDateTime(timestamp)), row["Logfile"]?.ToString() ?? "Reliability",
                        Convert.ToInt64(row["RecordNumber"], CultureInfo.InvariantCulture), provider, id, category, "Not supplied",
                        row["ProductName"]?.ToString() ?? provider, FirstLine(message, provider, id), message, "Source: Win32_ReliabilityRecords\n" + message));
                }
            }
            status.Add(read == 0 ? "Reliability history: no records returned; provider may be disabled or history empty" : $"Reliability history: {Math.Min(read, 5000)} records{(read > 5000 ? " · limit reached" : string.Empty)}");
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException or OverflowException)
        { status.Add($"Reliability history: unavailable / partial ({ex.GetType().Name})"); }
        // Prefer native log records, which include severity and structured data, over their WMI copies.
        var distinct = events.DistinctBy(item => $"{item.Log}|{item.RecordId}|{item.Timestamp.ToUnixTimeSeconds()}", StringComparer.OrdinalIgnoreCase);
        return new(now, ReliabilityAnalysis.Deduplicate(distinct.Where(item => item.Timestamp >= cutoff && item.Timestamp <= end)), status) { TruncatedAt = truncated };

        static string Utc(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        static string Stamp(DateTimeOffset? time) => time?.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "the cap";
    }

    internal static string Component(string raw, string fallback)
    {
        XElement xml;
        try { xml = XElement.Parse(raw); }
        catch (System.Xml.XmlException) { return fallback; }
        var data = xml.Descendants().Where(element => element.Name.LocalName == "Data").ToArray();
        if (fallback.Equals("Windows Error Reporting", StringComparison.OrdinalIgnoreCase))
        {
            var kind = data.FirstOrDefault(element => (string?)element.Attribute("Name") == "EventName")?.Value;
            var signature = data.FirstOrDefault(element => (string?)element.Attribute("Name") == "P1")?.Value;
            if (!string.IsNullOrWhiteSpace(kind)) return $"{kind} · {signature ?? "Signature unavailable"}";
        }
        foreach (var name in new[] { "AppName", "AppPath", "DeviceName", "DeviceInstanceId", "DriverName", "updateTitle", "ProductName", "param1" })
        {
            var value = data.FirstOrDefault(element => string.Equals((string?)element.Attribute("Name"), name, StringComparison.OrdinalIgnoreCase))?.Value;
            if (!string.IsNullOrWhiteSpace(value)) return value.Length > 180 ? value[..180] : value;
        }
        return fallback;
    }
    private static string FirstLine(string message, string provider, int id)
    {
        var line = message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return string.IsNullOrEmpty(line) ? $"{provider} · Event {id}" : line.Length > 160 ? line[..160] + "…" : line;
    }
}



