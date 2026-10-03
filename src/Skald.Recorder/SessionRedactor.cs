using Skald.Core.Models;
using Skald.Triage;

namespace Skald.Recorder;

// The recording counterpart of TriageRedactor: removes the computer name, user and domain names, profile paths, SIDs and device
// serials before a recording leaves the machine. Deep traces (ETL) cannot be scrubbed this way and are never part of a redacted export.
public static class SessionRedactor
{
    private const string Computer = "<computer>";

    // Built-in Windows accounts identify no one and are kept, so system and service processes stay recognizable.
    private static readonly string[] ServiceDomains = ["NT AUTHORITY", "NT SERVICE", "Window Manager", "Font Driver Host", "IIS APPPOOL"];

    public static SessionDocument Redact(SessionDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var computer = document.Metadata.MachineName;
        var accounts = document.Samples.SelectMany(sample => sample.Processes).Select(process => process.UserName)
            .Where(name => !string.IsNullOrWhiteSpace(name)).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => name!.Split('\\', 2)).Where(parts => parts.Length == 2 && !IsServiceDomain(parts[0])).ToArray();
        var users = accounts.Select(parts => parts[1]).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var domains = accounts.Select(parts => parts[0]).Where(domain => !domain.Equals(computer, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var cache = new Dictionary<string, string>(StringComparer.Ordinal);

        string Scrub(string text)
        {
            if (cache.TryGetValue(text, out var done)) return done;
            var result = TriageRedactor.Redact(text, computer, users.FirstOrDefault() ?? string.Empty, domains.FirstOrDefault());
            foreach (var user in users.Skip(1)) result = TriageRedactor.Redact(result, computer, user);
            foreach (var domain in domains.Skip(1)) result = TriageRedactor.Redact(result, computer, string.Empty, domain);
            cache[text] = result;
            return result;
        }
        string? ScrubUser(string? account)
        {
            if (account is null) return null;
            var parts = account.Split('\\', 2);
            return parts.Length == 2 && IsServiceDomain(parts[0]) ? account : "<user>";
        }

        return document with
        {
            Metadata = document.Metadata with { MachineName = Computer },
            Samples = document.Samples.Select(sample => sample with
            {
                Processes = sample.Processes.Select(process => process with
                {
                    Name = Scrub(process.Name),
                    ExecutablePath = process.ExecutablePath is null ? null : Scrub(process.ExecutablePath),
                    UserName = ScrubUser(process.UserName)
                }).ToArray(),
                Network = sample.Network with { Interfaces = sample.Network.Interfaces.Select(item => item with { Name = Scrub(item.Name) }).ToArray() },
                Sensors = sample.Sensors.Select(sensor => sensor with { Device = Scrub(sensor.Device), Id = Scrub(sensor.Id) }).ToArray()
            }).ToArray(),
            Events = document.Events.Select(item => item with
            {
                Note = item.Note is null ? null : Scrub(item.Note),
                ProcessName = item.ProcessName is null ? null : Scrub(item.ProcessName)
            }).ToArray(),
            Machine = document.Machine is not { } machine ? null : machine with
            {
                Details = machine.Details.Select(item => item.Label is "System name" or "Serial number"
                    ? new InventoryDetail(item.Label, "<redacted>") : new InventoryDetail(item.Label, Scrub(item.Value))).ToArray(),
                Drivers = machine.Drivers.Select(driver => driver with { Name = Scrub(driver.Name), DeviceId = TriageRedactor.RedactDeviceId(driver.DeviceId) }).ToArray(),
                SourceStatus = machine.SourceStatus.Select(Scrub).ToArray()
            },
            WindowsEvidence = document.WindowsEvidence is not { } evidence ? null : evidence with
            {
                Events = evidence.Events.Select(item => item with
                {
                    Component = Scrub(item.Component), Summary = Scrub(item.Summary), Details = Scrub(item.Details), Raw = Scrub(item.Raw)
                }).ToArray(),
                Dumps = evidence.Dumps.Select(file => file with { Path = Scrub(file.Path) }).ToArray(),
                SourceStatus = evidence.SourceStatus.Select(Scrub).ToArray()
            },
            Traces = [],
            Redacted = true
        };
    }

    private static bool IsServiceDomain(string domain) => ServiceDomains.Contains(domain, StringComparer.OrdinalIgnoreCase);
}
