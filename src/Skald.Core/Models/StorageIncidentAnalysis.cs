using System.Text.RegularExpressions;

namespace Skald.Core.Models;

public sealed record StorageEventLink(uint? DiskNumber, string Description, HardwareMatchConfidence Confidence);

public static partial class StorageIncidentAnalysis
{
    // Inbox storage stack plus common vendor port/miniport drivers (Intel RST/VMD, AMD RAID, LSI/Broadcom, vendor NVMe, Storage Spaces).
    private static readonly string[] StorageDrivers =
        ["storahci", "stornvme", "storport", "iaStor", "nvme", "atapi", "amdsata", "amdsbs", "rcraid", "megasas", "lsi_sa", "percsas", "storvsc", "spaceport"];

    public static bool IsStorageProvider(string provider)
        => provider.Equals("disk", StringComparison.OrdinalIgnoreCase) || StorageDrivers.Any(name => provider.Contains(name, StringComparison.OrdinalIgnoreCase));

    public static StorageEventLink? Link(ReliabilityEvent item)
    {
        if (!IsStorageProvider(item.Provider)) return null;
        if (item.EventId is not (7 or 9 or 11 or 15 or 51 or 52 or 129 or 153 or 157)) return null;

        var description = item.EventId switch
        {
            7 => "Bad block report",
            9 => "Device timeout",
            15 => "Device not ready",
            52 => "Predicted failure (SMART)",
            11 => "Controller error report",
            51 => "Paging I/O error",
            129 => "Storage request timeout/reset",
            153 => "I/O retry",
            157 => "Disk surprise removal",
            _ => "Storage driver report"
        };

        // Match only explicit disk identifiers. A RaidPort, LBA, or volume number is not a disk number.
        var text = item.Details + "\n" + item.Summary;
        var matches = DiskNumberPattern().Matches(text).Cast<Match>()
            .Concat(HarddiskPathPattern().Matches(text).Cast<Match>())
            .Concat(SurpriseRemovalPattern().Matches(text).Cast<Match>())
            .Select(match => uint.TryParse(match.Groups[1].Value, out var number) ? (uint?)number : null)
            .Where(number => number.HasValue).Select(number => number!.Value).Distinct().ToArray();
        return matches.Length == 1
            ? new(matches[0], description, HardwareMatchConfidence.Probable)
            : new(null, description + (matches.Length > 1 ? " (conflicting disk numbers)" : " (no disk identity)"), HardwareMatchConfidence.Unmapped);
    }

    [GeneratedRegex(@"\bfor\s+Disk\s+(\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DiskNumberPattern();

    [GeneratedRegex(@"\\Device\\Harddisk(\d+)\\DR\d+\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HarddiskPathPattern();

    [GeneratedRegex(@"\bDisk\s+(\d+)\s+has\s+been\s+surprise\s+removed\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SurpriseRemovalPattern();
}
