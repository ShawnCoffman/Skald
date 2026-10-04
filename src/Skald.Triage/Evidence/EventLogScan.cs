using System.Diagnostics.Eventing.Reader;
using Skald.Core.Models;

namespace Skald.Triage;

// The one way Skald reads a Windows event log: newest first, up to a cap, with a per-record timeout and per-record fault isolation,
// so the reliability history and the device evidence cannot drift apart in how they treat an unreadable record or a missing message.
internal static class EventLogScan
{
    // Read: records returned by the query; Kept: records turned into events; Skipped: records whose XML or message could not be read.
    // OldestRead is the oldest record the query returned (not the oldest in the log), which is where a capped read stopped.
    public sealed record Result(int Read, int Kept, int Skipped, DateTimeOffset? OldestRead, bool CapReached);

    public static string Severity(byte? level) => level switch { 1 => "Critical", 2 => "Error", 3 => "Warning", _ => "Information" };

    // `classify` names a record's category from its provider and id; null skips the record before its XML is read. `keep` filters
    // on the raw XML after it is read. Opening or reading the log throws EventLogException, UnauthorizedAccessException or
    // SecurityException to the caller, who decides how to report an unreadable log.
    public static Result Read(string log, string xpath, int cap, Func<string, int, string?> classify, Func<string, bool>? keep, List<ReliabilityEvent> events)
    {
        using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, xpath) { ReverseDirection = true });
        int read = 0, kept = 0, skipped = 0;
        DateTimeOffset? oldest = null;
        while (read < cap)
        {
            using var record = reader.ReadEvent(TimeSpan.FromSeconds(5));
            if (record is null) break;
            read++;
            if (record.TimeCreated is not { } created) continue;
            oldest = new DateTimeOffset(created);
            var provider = record.ProviderName ?? "Unknown";
            if (classify(provider, record.Id) is not { } category) continue;
            // One unreadable record is skipped and counted; it must not abort the whole log.
            try
            {
                var raw = record.ToXml();
                if (keep is not null && !keep(raw)) continue;
                string message;
                try { message = record.FormatDescription() ?? "No formatted message available. See raw event XML."; }
                catch (EventLogException) { message = "Message resources unavailable. See raw event XML."; }
                events.Add(new(oldest.Value, log, record.RecordId ?? 0, provider, record.Id, category, Severity(record.Level),
                    WindowsReliabilityCollector.Component(raw, provider), WindowsReliabilityCollector.FirstLine(message, provider, record.Id), message, raw));
                kept++;
            }
            catch (Exception ex) when (ex is EventLogException or System.Xml.XmlException or InvalidOperationException) { skipped++; }
        }
        return new(read, kept, skipped, oldest, read == cap);
    }

    // The oldest record the log holds at all, for saying how far back a clean result reaches. Null when the log is empty.
    public static DateTimeOffset? OldestRecord(string log)
    {
        using var reader = new EventLogReader(new EventLogQuery(log, PathType.LogName, "*"));
        using var record = reader.ReadEvent(TimeSpan.FromSeconds(5));
        return record?.TimeCreated is { } created ? new DateTimeOffset(created) : null;
    }
}
