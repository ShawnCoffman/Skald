using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Management;
using System.Xml.Linq;
using Skald.Core.Models;

namespace Skald.Collectors;

public static class WindowsReliabilityCollector
{
    public static Task<ReliabilityHistory> CollectAsync() => Task.Run(Collect);

    private static ReliabilityHistory Collect()
    {
        var now = DateTimeOffset.Now;
        var cutoff = now.AddDays(-30);
        var events = new List<ReliabilityEvent>();
        var status = new List<string>();
        var nativeLogs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var log in new[] { "System", "Application" })
        {
            var read = 0;
            try
            {
                var query = new EventLogQuery(log, PathType.LogName, "*[System[TimeCreated[timediff(@SystemTime) <= 2592000000] and (Level=1 or Level=2 or Level=3 or Provider[@Name='Microsoft-Windows-WindowsUpdateClient'] or Provider[@Name='MsiInstaller'] or Provider[@Name='Windows Error Reporting'] or Provider[@Name='Microsoft-Windows-WHEA-Logger'])]]") { ReverseDirection = true };
                using var reader = new EventLogReader(query);
                while (read < 5000)
                {
                    using var record = reader.ReadEvent(TimeSpan.FromSeconds(5));
                    if (record is null) break;
                    read++;
                    var provider = record.ProviderName ?? "Unknown";
                    var category = ReliabilityAnalysis.Category(provider, record.Id);
                    if (category is null || record.TimeCreated is not { } created) continue;
                    var raw = record.ToXml();
                    var component = Component(raw, provider);
                    string message;
                    try { message = record.FormatDescription() ?? "No formatted message available. See raw event XML."; }
                    catch (EventLogException) { message = "Message resources unavailable. See raw event XML."; }
                    events.Add(new(new DateTimeOffset(created), log, record.RecordId ?? 0, provider, record.Id, category,
                        record.Level switch { 1 => "Critical", 2 => "Error", 3 => "Warning", _ => "Information" }, component,
                        FirstLine(message, provider, record.Id), message, raw));
                }
                status.Add($"{log}: {read} records scanned{(read == 5000 ? " · limit reached; older records may be omitted" : string.Empty)}");
                nativeLogs.Add(log);
            }
            catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or System.Security.SecurityException)
            { status.Add($"{log}: unavailable / partial ({ex.GetType().Name})"); }
        }
        try
        {
            var date = ManagementDateTimeConverter.ToDmtfDateTime(cutoff.UtcDateTime);
            using var searcher = new ManagementObjectSearcher("root\\CIMV2", $"SELECT TimeGenerated,Logfile,RecordNumber,SourceName,EventIdentifier,ProductName,Message FROM Win32_ReliabilityRecords WHERE TimeGenerated >= '{date}'", new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(10) });
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
        return new(now, ReliabilityAnalysis.Deduplicate(distinct.Where(item => item.Timestamp >= cutoff && item.Timestamp <= now)), status);
    }

    internal static string Component(string raw, string fallback)
    {
        var xml = XElement.Parse(raw);
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



