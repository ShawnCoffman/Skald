using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Skald.Core.Models;

public static partial class CrashTriage
{
    public static string Describe(ReliabilityEvent report, IReadOnlyList<CrashDumpFile> dumps)
    {
        var parts = new List<string>();
        if (report.Category == "Hardware")
            parts.Add("WHEA reports a hardware error source. Its source is not necessarily the replaceable failing part.");
        if (report.Category == "Crashes & restarts")
            parts.Add(report.EventId is 41 or 6008
                ? "An unclean restart was reported. This event does not establish a bugcheck or a power-supply fault."
                : "Inspect the stop code and a matching dump before assigning a driver or hardware cause.");

        var data = StructuredData(report.Raw);
        if (data.Count > 0) parts.Add("Structured report fields: " + string.Join(" · ", data.Select(pair => $"{pair.Key}={pair.Value}")));
        var stop = report.Category == "Crashes & restarts" && (report.Details.Contains("bugcheck", StringComparison.OrdinalIgnoreCase)
            || report.Details.Contains("stop code", StringComparison.OrdinalIgnoreCase))
            ? StopCodePattern().Match(report.Details) : Match.Empty;
        if (stop.Success) parts.Add($"Reported stop code: {stop.Groups[1].Value}");

        var candidate = CandidateDump(report, dumps);
        parts.Add(candidate is null
            ? "No dump file with a nearby file time was found in the scanned locations. Missing or inaccessible dumps remain possible."
            : $"Nearby dump candidate: {candidate.Kind} · {candidate.ModifiedAt.ToLocalTime():g} · {candidate.Path}. File time alone does not prove it belongs to this report.");
        return string.Join("\n\n", parts);
    }

    public static CrashDumpFile? CandidateDump(ReliabilityEvent report, IReadOnlyList<CrashDumpFile> dumps)
        => dumps.Where(file => Math.Abs((file.ModifiedAt - report.Timestamp).TotalHours) <= 2)
            .MinBy(file => Math.Abs((file.ModifiedAt - report.Timestamp).TotalSeconds));

    public static IReadOnlyDictionary<string, string> StructuredData(string raw)
    {
        try
        {
            var xml = XElement.Parse(raw);
            return xml.Descendants().Where(element => element.Name.LocalName == "Data")
                .Select(element => (Name: (string?)element.Attribute("Name"), Value: element.Value.Trim()))
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Name) && pair.Value.Length > 0 && pair.Value.Length <= 120
                    && !pair.Name.Equals("RawData", StringComparison.OrdinalIgnoreCase))
                .GroupBy(pair => pair.Name!, StringComparer.OrdinalIgnoreCase)
                .Take(12).ToDictionary(group => group.Key, group => group.First().Value, StringComparer.OrdinalIgnoreCase);
        }
        catch (System.Xml.XmlException) { return new Dictionary<string, string>(); }
    }

    [GeneratedRegex(@"(?:bugcheck(?:\s+(?:code|was))?|stop\s+code)\s*[:=]?\s*(0x[0-9a-fA-F]{2,8})\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StopCodePattern();
}
