using System.Text.RegularExpressions;
using Skald.Core.Models;

namespace Skald.Triage;

// Removes the identifiers a tech does not want in a ticket attachment or a vendor case: computer name, serial number, user names, SIDs.
public static partial class TriageRedactor
{
    public static TriageReport Redact(TriageReport report, string? computerName = null, string? userName = null)
    {
        var computer = computerName ?? Environment.MachineName;
        var user = userName ?? Environment.UserName;
        var domain = computerName is null ? Environment.UserDomainName : null;
        string Scrub(string text) => Redact(text, computer, user, domain);
        return report with
        {
            Machine = report.Machine.Select(item => item.Label is "System name" or "Serial number"
                ? new InventoryDetail(item.Label, "<redacted>") : new InventoryDetail(item.Label, Scrub(item.Value))).ToArray(),
            Verdict = report.Verdict with { Detail = Scrub(report.Verdict.Detail), Headline = Scrub(report.Verdict.Headline), Leads = report.Verdict.Leads.Select(Scrub).ToArray() },
            Checks = report.Checks.Select(check => check with
            {
                Summary = Scrub(check.Summary), Details = check.Details.Select(Scrub).ToArray(), Limit = check.Limit is null ? null : Scrub(check.Limit)
            }).ToArray(),
            NotCovered = report.NotCovered.Select(Scrub).ToArray(),
            SourceStatus = report.SourceStatus.Select(Scrub).ToArray()
        };
    }

    // Generic account names would also match ordinary words ("Run as administrator"); they are still removed from profile paths.
    private static readonly string[] GenericAccounts = ["administrator", "admin", "user", "guest", "owner", "default", "public"];

    public static string Redact(string text, string computerName, string userName, string? domainName = null)
    {
        var result = ProfilePath().Replace(text, "<user>");
        result = Sid().Replace(result, "<sid>");
        result = DeviceInstancePath().Replace(result, match => RedactDeviceId(match.Value));
        result = MacAddress().Replace(result, "<mac>");
        if (computerName.Length >= 2)
        {
            var name = Regex.Escape(computerName);
            // The bare name is matched case-sensitively as a whole token, so ordinary words ("desktop", "Remote Desktop") and
            // vendor names survive. As a DNS label or UNC host it is matched in any case.
            result = Regex.Replace(result, $@"(?<![A-Za-z0-9-]){name}(?![A-Za-z0-9-])", "<computer>", RegexOptions.CultureInvariant);
            result = Regex.Replace(result, $@"(?<![A-Za-z0-9-]){name}(?=\.[A-Za-z])", "<computer>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            result = Regex.Replace(result, $@"(?<=\\\\){name}(?![A-Za-z0-9-])", "<computer>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        if (domainName is { Length: >= 3 } domain && !domain.Equals(computerName, StringComparison.OrdinalIgnoreCase))
            result = Regex.Replace(result, $@"\b{Regex.Escape(domain)}\b", "<domain>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (userName.Length >= 3 && !GenericAccounts.Contains(userName, StringComparer.OrdinalIgnoreCase))
            result = Regex.Replace(result, $@"\b{Regex.Escape(userName)}\b", "<user>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return result;
    }

    // Device instance IDs can carry a serial number (usually the last segment) or a Bluetooth MAC address (often a middle
    // segment, e.g. BTHENUM\DEV_542A4316F033\...). The enumerator, VID/PID and class segments stay, so the device type is still clear.
    public static string RedactDeviceId(string deviceId)
    {
        var segments = deviceId.Split('\\');
        if (segments.Length >= 3) segments[^1] = "<instance>";
        return string.Join('\\', segments.Select(segment => EmbeddedMac().Replace(segment, "<mac>")));
    }

    public static DriverInventory Redact(DriverInventory inventory, string? computerName = null, string? userName = null)
    {
        var computer = computerName ?? Environment.MachineName;
        var user = userName ?? Environment.UserName;
        var domain = computerName is null ? Environment.UserDomainName : null;
        return inventory with
        {
            Drivers = inventory.Drivers.Select(driver => driver with
            {
                DeviceId = RedactDeviceId(driver.DeviceId),
                Name = Redact(driver.Name, computer, user, domain)
            }).ToArray()
        };
    }

    [GeneratedRegex(@"\b(?:USB|USBSTOR|USB4|BTHENUM|BTHLE|BTHLEDEVICE|BTH|HID|SCSI|PCI|SWD|ACPI|HDAUDIO|INTELAUDIO|DISPLAY|MONITOR|STORAGE|ROOT|SD|NVME|UMB|WPDBUSENUM|TBT)\\[^\s""',;()<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeviceInstancePath();

    // Twelve hex digits not part of a longer hex run or a GUID group.
    [GeneratedRegex(@"(?<![0-9A-Fa-f-])[0-9A-Fa-f]{12}(?![0-9A-Fa-f])", RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedMac();

    [GeneratedRegex(@"\b(?:[0-9A-Fa-f]{2}[:-]){5}[0-9A-Fa-f]{2}\b", RegexOptions.CultureInvariant)]
    private static partial Regex MacAddress();

    [GeneratedRegex(@"(?<=\\Users\\)[^\\/:*?""<>|\r\n]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ProfilePath();

    // Domain and local accounts (S-1-5-21-...) and Microsoft Entra accounts (S-1-12-1-...).
    [GeneratedRegex(@"S-1-(?:5-21|12-1)(?:-\d+){2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Sid();
}
