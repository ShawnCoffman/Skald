using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using Skald.Core.Models;

namespace Skald.Triage;

// The device classes' own evidence: every device Windows lists for them (present or remembered), and the event records that
// belong to them. Events are filtered here so the report can show them without the tech opening Event Viewer.
public static class DeviceEvidenceCollector
{
    internal const string SystemLog = "System";
    internal const string PnpConfigurationLog = "Microsoft-Windows-Kernel-PnP/Configuration";
    internal const string NdisProvider = "Microsoft-Windows-NDIS";
    private const int ScanCap = 5000;

    public static Task<DeviceEvidence> CollectAsync(DateTimeOffset since) => Task.Run(() => Collect(since));

    private static DeviceEvidence Collect(DateTimeOffset since)
    {
        var status = new List<string>();
        var devices = new List<DeviceRecord>();
        var unreadable = new HashSet<DeviceClass>();
        foreach (var spec in DeviceCatalog.Classes)
            foreach (var classGuid in spec.ClassGuids)
            {
                var raw = PnpDeviceReader.Read(classGuid, out var problem);
                if (raw is null || problem is not null) unreadable.Add(spec.Class);
                var only = spec.ServiceFilters.GetValueOrDefault(classGuid);
                var records = (raw ?? []).Where(device => only is null || only.Contains(device.Service ?? string.Empty, StringComparer.OrdinalIgnoreCase)).Select(device => new DeviceRecord(device.InstanceId, device.Name, spec.Class, device.ClassName, device.Present,
                    device.ProblemCode, device.Service, device.Container == DeviceCatalog.LocalMachineContainer)).ToArray();
                devices.AddRange(records);
                var hardware = records.Where(device => spec.IsHardware(device.InstanceId)).ToArray();
                status.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{spec.Noun} ({records.FirstOrDefault()?.ClassName ?? DeviceCatalog.ClassLabel(classGuid)}): {records.Length} listed, {hardware.Count(device => device.Present == true)} hardware present, {hardware.Count(device => device.Present == false)} remembered but not present")
                    + (spec.AccessoryEnumerators.Count > 0 ? $", {records.Count(device => spec.IsAccessory(device.InstanceId) && device.Present == true)} paired-device entries present" : string.Empty)
                    + (problem is null ? string.Empty : $" · {problem}"));
            }

        // Kernel drivers log under their service name (Netwtw10, BTHUSB, i8042prt), so the present devices' services name the providers to read.
        var services = devices.Where(device => device.Present == true && DeviceCatalog.Spec(device.Class).IsHardware(device.InstanceId))
            .Select(device => device.Service).OfType<string>().Where(IsProviderName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var providers = services.Concat(DeviceCatalog.Classes.SelectMany(spec => spec.ExtraProviders)).Append(NdisProvider)
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(name => $"Provider[@Name='{name}']");
        var time = $"TimeCreated[@SystemTime >= '{since.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture)}']";
        var events = new List<ReliabilityEvent>();
        var logs = new List<LogCoverage>
        {
            ReadLog(SystemLog, $"*[System[(Level=1 or Level=2 or Level=3) and {time} and ({string.Join(" or ", providers)})]]", null, events, status)
        };
        var ids = devices.Select(device => device.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // 400: a driver was installed or updated on the device; 411: the device had a problem starting.
        logs.Add(ReadLog(PnpConfigurationLog, $"*[System[(EventID=400 or EventID=411) and {time}]]",
            raw => EventData.Named(raw).TryGetValue("DeviceInstanceId", out var id) && ids.Contains(id), events, status));
        return new(DateTimeOffset.Now, devices, events, status) { UnreadableClasses = unreadable, Logs = logs };
    }

    // Service names go into an XPath query, so anything but plain name characters is left out rather than escaped.
    private static bool IsProviderName(string name) => name.Length is > 0 and < 128 && name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.');

    private static LogCoverage ReadLog(string log, string xpath, Func<string, bool>? keep, List<ReliabilityEvent> events, List<string> status)
    {
        try
        {
            var oldest = EventLogScan.OldestRecord(log);
            var scan = EventLogScan.Read(log, xpath, ScanCap, (_, _) => "Devices", keep, events);
            status.Add($"{log}: {scan.Kept} device record(s) kept of {scan.Read} read" + (scan.CapReached ? " · limit reached" : string.Empty)
                + (scan.Skipped > 0 ? $" · {scan.Skipped} unreadable record(s) skipped" : string.Empty)
                + (oldest is { } start ? string.Create(CultureInfo.InvariantCulture, $" · log holds records from {start.ToLocalTime():yyyy-MM-dd}") : " · log is empty"));
            return new(log, true, oldest, scan.CapReached);
        }
        catch (Exception ex) when (ex is EventLogException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            status.Add($"{log}: unavailable ({ex.GetType().Name})");
            return new(log, false, null);
        }
    }
}
