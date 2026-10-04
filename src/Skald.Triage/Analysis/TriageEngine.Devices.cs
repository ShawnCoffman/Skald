using System.Globalization;
using System.Text.RegularExpressions;
using Skald.Core.Models;

namespace Skald.Triage;

// Per-class device checks (verdict), the Windows settings that can make a working device look broken (never the verdict),
// and what the evidence says about driver and BIOS updates.
public static partial class TriageEngine
{
    // Harmless records that a naive "driver warnings" rule would count. Intel Wi-Fi drivers log 6062 ("Lso was triggered")
    // at Warning level many times a month on healthy machines; BTHUSB 17 says the radio lacks an optional Bluetooth LE feature.
    private static bool BenignDeviceEvent(ReliabilityEvent item) => (NetwtwProvider().IsMatch(item.Provider) && item.EventId == 6062)
        || (item.Provider.Equals("BTHUSB", StringComparison.OrdinalIgnoreCase) && item.EventId == 17);

    // Windows' own audio engine and runtime modules. A crash of audiodg.exe in any other module is in code that came with a driver.
    private static readonly string[] WindowsAudioModules =
    [
        "audiodg.exe", "audioeng.dll", "audioses.dll", "audiokse.dll", "mmdevapi.dll", "wmalfxgfxdsp.dll", "ntdll.dll", "kernelbase.dll", "kernel32.dll",
        "ucrtbase.dll", "msvcrt.dll", "combase.dll", "rpcrt4.dll", "ole32.dll", "msvcp_win.dll", "msvcp140.dll", "vcruntime140.dll"
    ];

    // Device instance IDs the class checks own, so the generic Device Manager and driver checks do not count them a second time.
    // With accessories: the class hardware plus paired devices (whose problem codes the class check reports). Without: the hardware only.
    internal static HashSet<string> ClaimedDevices(TriageInputs inputs, bool accessories)
        => inputs.Devices is not { } evidence ? new(StringComparer.OrdinalIgnoreCase)
            : evidence.Devices.Where(device => DeviceCatalog.Spec(device.Class) is var spec && (spec.IsHardware(device.InstanceId) || (accessories && spec.IsAccessory(device.InstanceId))))
                .Select(device => device.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);

    // Work shared by every class check, done once per scan: the driver inventory and baseline indexed by device, the device evidence
    // records in the window, and the setup-log and load-failure records with their named fields parsed, so each class only filters.
    private sealed record DeviceContext(string? Label, Dictionary<string, DriverRecord>? Drivers, Dictionary<string, DriverRecord> Baseline, ReliabilityEvent[] InWindow,
        (ReliabilityEvent Item, IReadOnlyDictionary<string, string> Data, string Device)[] Setup, (ReliabilityEvent Item, string Device)[] LoadFailures);

    private static DeviceContext DeviceContextOf(TriageInputs inputs, IReadOnlyList<ReliabilityEvent> windowEvents, DateTimeOffset cutoff)
    {
        var inWindow = inputs.Devices?.Events.Where(item => item.Timestamp >= cutoff && item.Timestamp <= inputs.Now).ToArray() ?? [];
        var setup = inWindow.Where(item => item.Log == DeviceEvidenceCollector.PnpConfigurationLog)
            .Select(item => (Item: item, Data: EventData.Named(item.Raw)))
            .Select(pair => (pair.Item, pair.Data, Device: pair.Data.GetValueOrDefault("DeviceInstanceId") ?? string.Empty)).ToArray();
        var loadFailures = windowEvents.Where(item => item.Provider.Contains("Kernel-PnP", StringComparison.OrdinalIgnoreCase) && item.EventId == 219
                && !BenignDriverNames.Any(name => item.Summary.Contains(name, StringComparison.OrdinalIgnoreCase) || item.Raw.Contains(name, StringComparison.OrdinalIgnoreCase)))
            .Select(item => (Item: item, Device: EventData.Named(item.Raw).GetValueOrDefault("DriverName")))
            .Where(pair => pair.Device is not null).Select(pair => (pair.Item, pair.Device!)).ToArray();
        return new(BaselineLabel(inputs), DriverIndex(inputs.Drivers?.Drivers), DriverIndex(inputs.Baseline?.Drivers) ?? new(StringComparer.OrdinalIgnoreCase), inWindow, setup, loadFailures);
    }

    // Every record by device, not only a class's own: an audio controller is a System-class device.
    private static Dictionary<string, DriverRecord>? DriverIndex(IReadOnlyList<DriverRecord>? drivers)
        => drivers?.GroupBy(driver => driver.DeviceId, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

    // How a class's drivers compare with the baseline, for the update note.
    private sealed record DriverState(bool Compared, IReadOnlyList<string> Changes, string? Unavailable, string? Baseline = null);

    private sealed record DeviceOutcome(DeviceClassSpec Spec, TriageCheck Check, DriverState Drivers, string? Coverage);

    private static DeviceOutcome DeviceClassCheck(TriageInputs inputs, DeviceClassSpec spec, DeviceContext context, AppFault[] faults)
    {
        var cutoff = inputs.Now.AddDays(-inputs.WindowDays);
        var (id, title) = (spec.Id, spec.Title);
        var noDrivers = new DriverState(false, [], "the device list could not be read");
        if (inputs.Devices is not { } evidence)
            return new(spec, new(id, title, CheckDomain.HardwareDriverFirmware, CheckStatus.CouldNotCheck, "The Windows device list could not be read.", []), noDrivers, null);

        var listed = evidence.Devices.Where(device => device.Class == spec.Class && spec.IsHardware(device.InstanceId)).ToArray();
        var present = listed.Where(device => device.Present == true).ToArray();
        var details = new List<string>();
        var limits = new List<string>();
        var parts = new List<string>();
        var significant = false;

        // 1. Present and enumerating: a device that was in the baseline and is gone, or a built-in device Windows remembers but cannot see.
        var presentIds = present.Select(device => device.InstanceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var presentKeys = present.Select(device => device.HardwareKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var label = context.Label;
        var baselineDevices = inputs.Baseline?.Drivers.Where(driver => spec.SnapshotClasses.Contains(driver.Class, StringComparer.OrdinalIgnoreCase) && spec.IsHardware(driver.DeviceId))
            .DistinctBy(driver => driver.DeviceId, StringComparer.OrdinalIgnoreCase).ToArray() ?? [];
        var external = 0;
        var gone = 0;
        foreach (var before in baselineDevices.Where(driver => !presentIds.Contains(driver.DeviceId)))
        {
            // The same part listed under a new instance ID (a firmware or BIOS update can change its revision) has not gone anywhere.
            if (presentKeys.Contains(DeviceRecord.HardwareKeyOf(before.DeviceId))) continue;
            var remembered = listed.FirstOrDefault(device => device.InstanceId.Equals(before.DeviceId, StringComparison.OrdinalIgnoreCase));
            if (remembered is { BuiltIn: true })
                details.Add($"{before.Name}: present in {label}, not present now. Windows still lists it as built into this computer, so it has stopped enumerating (failed, disconnected inside the machine, or turned off in BIOS setup).");
            else if (remembered is null && DeviceCatalog.InternalEnumerators.Contains(before.DeviceId.Split('\\')[0], StringComparer.OrdinalIgnoreCase))
                details.Add($"{before.Name}: present in {label}, and Windows no longer lists it at all (removed or uninstalled, failed, or turned off in BIOS setup).");
            else { external++; continue; }
            gone++;
            significant = true;
        }
        if (gone > 0) parts.Add($"{gone} {spec.Noun}(s) no longer present");
        if (external > 0) limits.Add($"{external} external {spec.Noun}(s) from {label} are not connected now; that is expected for unplugged devices and is not counted.");
        var baselineIds = baselineDevices.Select(driver => driver.DeviceId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var phantoms = listed.Where(device => device is { Present: false, BuiltIn: true } && !presentKeys.Contains(device.HardwareKey) && !baselineIds.Contains(device.InstanceId)).ToArray();
        foreach (var device in phantoms)
            details.Add($"{device.Name}: Windows remembers this built-in {spec.Noun}, but it is not present now. It may be a part that was replaced, turned off in BIOS setup, or one that stopped enumerating.");
        if (phantoms.Length > 0) parts.Add($"{phantoms.Length} built-in {spec.Noun}(s) remembered but not present");

        // 2. What Windows reports for its health. A disabled device is a setting, shown in the settings section instead.
        foreach (var device in present.Where(device => device.HasProblem))
        {
            details.Add(new DeviceProblem(device.Name, device.ProblemCode!.Value, device.InstanceId).Description);
            significant = true;
        }
        var problems = present.Count(device => device.HasProblem);
        if (problems > 0) parts.Add($"{problems} with a Device Manager problem");
        var unknownStatus = present.Count(device => device.ProblemCode is null);
        if (unknownStatus > 0) limits.Add($"The status of {unknownStatus} {spec.Noun}(s) could not be read.");
        var disabled = present.Count(device => device.Disabled);
        if (disabled > 0) limits.Add($"{disabled} {spec.Noun}(s) disabled in Device Manager; that is a setting, listed under Windows settings, and not counted here.");
        // A paired device's problem is about that accessory (its profile driver), not the radio, so it is minor.
        var accessories = evidence.Devices.Where(device => device.Class == spec.Class && spec.IsAccessory(device.InstanceId) && device.HasProblem).ToArray();
        foreach (var device in accessories) details.Add("Paired device · " + new DeviceProblem(device.Name, device.ProblemCode!.Value, device.InstanceId).Description);
        if (accessories.Length > 0) parts.Add($"{accessories.Length} paired device(s) with a Device Manager problem");

        // 3. The driver of each present device, against the baseline. A change is a detail, never evidence on its own.
        var drivers = context.Drivers;
        var baselineDrivers = context.Baseline;
        var changes = new List<string>();
        var driverLines = new List<string>();
        var unsigned = 0;
        foreach (var device in present.Where(device => !device.Disabled))
        {
            if (drivers is null || !drivers.TryGetValue(device.InstanceId, out var driver)) { driverLines.Add($"Driver · {device.Name}: not in the driver inventory."); continue; }
            var comparison = Comparison(inputs, driver, baselineDrivers, label);
            if (comparison.StartsWith("changed", StringComparison.Ordinal)) changes.Add($"{device.Name}: {comparison}");
            driverLines.Add(DriverLine("Driver", device.Name, driver, comparison));
            if (driver.IsSigned == false) { unsigned++; significant = true; }
        }
        if (unsigned > 0) parts.Add($"{unsigned} unsigned driver(s)");
        // Drivers shown for reference only (the graphics adapter beside the monitors); their evidence is counted by another check.
        foreach (var driver in (inputs.Drivers?.Drivers ?? []).Where(driver => spec.ReferenceClasses.Contains(driver.Class, StringComparer.OrdinalIgnoreCase)
                     && driver.DeviceId.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase)))
            driverLines.Add(DriverLine("Graphics adapter driver (evidence under Graphics driver and GPU)", driver.Name, driver, Comparison(inputs, driver, baselineDrivers, label)));
        var driverState = DriverStateOf(inputs, changes, label);

        // 4. Driver errors and resets in the window, filtered for the tech: the device's own driver service, NDIS adapter resets,
        // devices that failed to start, and drivers that failed to load.
        var services = present.Where(device => device.Service is not null).GroupBy(device => device.Service!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(device => device.Name).First(), StringComparer.OrdinalIgnoreCase);
        if (present.FirstOrDefault(device => !device.Disabled) is { } owner)
            foreach (var provider in spec.ExtraProviders) services.TryAdd(provider, owner.Name);
        var inWindow = context.InWindow;
        var driverEvents = inWindow.Where(item => item.Log == DeviceEvidenceCollector.SystemLog && services.ContainsKey(item.Provider)
            && (!spec.ExtraProviders.Contains(item.Provider, StringComparer.OrdinalIgnoreCase) || item.Severity is "Error" or "Critical")).ToArray();
        var benign = driverEvents.Where(BenignDeviceEvent).ToArray();
        var ignored = benign.Length;
        var counted = driverEvents.Where(item => !BenignDeviceEvent(item)).ToArray();
        var errors = counted.Count(item => item.Severity is "Error" or "Critical");
        foreach (var group in counted.GroupBy(item => (item.Provider, item.EventId)).OrderByDescending(group => group.Count()).Take(MaxLines))
        {
            var latest = group.MaxBy(item => item.Timestamp)!;
            details.Add($"{latest.Provider} ({services[latest.Provider]} driver) {latest.Severity.ToLowerInvariant()} event {latest.EventId} ×{group.Count()}, latest {Stamp(latest.Timestamp)}: {Text(latest)}");
        }
        if (counted.Length > 0) parts.Add($"{counted.Length} driver error/warning record(s)");
        if (errors >= 3) significant = true;

        // NDIS names the adapter in its message; resets of virtual adapters (Wi-Fi Direct, WAN miniports) are not hardware evidence.
        var resets = inWindow.Where(item => item.Provider.Equals(DeviceEvidenceCollector.NdisProvider, StringComparison.OrdinalIgnoreCase) && item.EventId is 10400 or 10317)
            .Select(item => (Item: item, Device: present.FirstOrDefault(device => item.Details.Contains(BaseName(device.Name), StringComparison.OrdinalIgnoreCase))))
            .Where(pair => pair.Device is not null).ToArray();
        foreach (var group in resets.GroupBy(pair => (pair.Device!.Name, pair.Item.EventId)))
        {
            var latest = group.MaxBy(pair => pair.Item.Timestamp).Item;
            var reason = ResetReason().Match(latest.Details) is { Success: true } match ? match.Groups[1].Value.Trim() : null;
            details.Add(latest.EventId == 10400
                ? $"{group.Key.Name}: adapter reset ×{group.Count()}, latest {Stamp(latest.Timestamp)}" + (reason is null ? "." : $". Reason given: {reason}.")
                : $"{group.Key.Name}: adapter fatal error ×{group.Count()}, latest {Stamp(latest.Timestamp)}: {Text(latest)}");
        }
        if (resets.Length > 0) parts.Add($"{resets.Length} adapter reset/fatal-error record(s)");
        if (resets.Length >= 3) significant = true;

        // The device setup log covers every device; only this class's hardware belongs here.
        var ids = listed.ToDictionary(device => device.InstanceId, device => device.Name, StringComparer.OrdinalIgnoreCase);
        var setup = context.Setup.Where(pair => ids.ContainsKey(pair.Device)).ToArray();
        var failedStarts = setup.Where(pair => pair.Item.EventId == 411).ToArray();
        foreach (var group in failedStarts.GroupBy(pair => pair.Device, StringComparer.OrdinalIgnoreCase))
        {
            var latest = group.MaxBy(pair => pair.Item.Timestamp);
            var problem = latest.Data.GetValueOrDefault("Problem");
            details.Add($"{ids[group.Key]}: had a problem starting ×{group.Count()}, latest {Stamp(latest.Item.Timestamp)}" + (problem is null ? "." : $" (problem {problem})."));
            significant = true;
        }
        if (failedStarts.Length > 0) parts.Add($"{failedStarts.Length} failed device start(s)");

        // Sound enhancements (APOs) ship with the audio driver and run inside Windows' audio engine, so an audio engine crash
        // inside a module that is not Windows' own points at the driver package rather than at the application playing sound.
        if (spec.Class == DeviceClass.Audio)
        {
            // Vendor APOs are installed into System32, so the module path cannot tell them apart; the name of Windows' own modules can.
            var engine = faults.Where(fault => !fault.IsHang && fault.App.Equals("audiodg.exe", StringComparison.OrdinalIgnoreCase) && fault.Module is { } module
                && !WindowsAudioModules.Contains(module, StringComparer.OrdinalIgnoreCase)).ToArray();
            foreach (var group in engine.GroupBy(fault => fault.Module!, StringComparer.OrdinalIgnoreCase))
                details.Add($"Windows audio engine (audiodg.exe) crashed inside {group.Key} ×{group.Count()}, latest {Stamp(group.Max(fault => fault.Time))}. A module that is not Windows' own inside the audio engine is usually a sound enhancement (APO) installed with the audio driver.");
            if (engine.Length > 0) parts.Add($"{engine.Length} audio engine crash(es) in a driver-supplied module");
            if (engine.Length >= 3) significant = true;
            if (!LogReadable(inputs.Reliability, "Application")) limits.Add("The Application event log could not be read, so audio engine crashes were not checked.");
        }
        var loadFailures = context.LoadFailures.Where(pair => ids.ContainsKey(pair.Device)).ToArray();
        foreach (var group in loadFailures.GroupBy(pair => pair.Device, StringComparer.OrdinalIgnoreCase))
            details.Add($"{ids[group.Key]}: driver failed to load ×{group.Count()}, latest {Stamp(group.Max(pair => pair.Item.Timestamp))}.");
        if (loadFailures.Length > 0) parts.Add($"{loadFailures.Length} driver load failure(s)");

        var installs = setup.Where(pair => pair.Item.EventId == 400 && pair.Data.GetValueOrDefault("DeviceUpdated")?.Equals("true", StringComparison.OrdinalIgnoreCase) == true).ToArray();
        foreach (var (item, data, device) in installs.OrderByDescending(pair => pair.Item.Timestamp).Take(3))
            driverLines.Add($"Driver installed · {ids[device]}: {data.GetValueOrDefault("DriverProvider")} {data.GetValueOrDefault("DriverVersion")} on {Stamp(item.Timestamp)}.");

        // What the event records cover.
        var system = evidence.Log(DeviceEvidenceCollector.SystemLog);
        var systemReadable = system is { Readable: true };
        var coverage = default(string);
        if (!systemReadable) limits.Add("The System event log could not be read, so driver errors and adapter resets were not checked.");
        else coverage = Coverage("The System log", system!, cutoff, inputs);
        if (coverage is not null) limits.Add(coverage);
        if (evidence.Log(DeviceEvidenceCollector.PnpConfigurationLog) is not { Readable: true } configuration)
            limits.Add("Windows' device setup log (Kernel-PnP/Configuration) could not be read, so failed device starts and driver installs were not checked.");
        else if (Coverage("The device setup log", configuration, cutoff, inputs) is { } setupCoverage) limits.Add(setupCoverage);
        if (ignored > 0)
            limits.Add($"{ignored} routine driver warning(s) that also appear on healthy machines were ignored ({string.Join(", ", benign.Select(item => $"{item.Provider} {item.EventId.ToString(CultureInfo.InvariantCulture)}").Distinct())}).");
        var classUnreadable = evidence.UnreadableClasses.Contains(spec.Class);
        if (classUnreadable) limits.Add($"The Windows list of {spec.Noun}s could not be read completely (see Sources).");

        var found = details.Count > 0;
        string Limit(string fallback) => string.Join(" ", limits.Prepend(fallback));
        DeviceOutcome Outcome(CheckStatus status, string summary, IEnumerable<string> lines, bool minor = false)
            => new(spec, new(id, title, CheckDomain.HardwareDriverFirmware, status, summary, Cap(Collapse(lines), MaxLines + 4), Limit(spec.NotProven), minor), driverState, coverage);

        if (found)
            return Outcome(CheckStatus.Found, Sentence(string.Join("; ", parts)), details.Concat(driverLines), minor: !significant);
        // Nothing found only counts when everything was read: the device list, each present device's status, and the driver history.
        if (classUnreadable || unknownStatus > 0 || !systemReadable)
            return Outcome(CheckStatus.CouldNotCheck, classUnreadable ? $"The {spec.Noun} list could not be read completely, so a missing or failed device could be overlooked."
                : unknownStatus > 0 ? $"The status of {unknownStatus} of {present.Length} present {spec.Noun}(s) could not be read, so a Device Manager problem could be overlooked."
                : $"Device status was read ({present.Length} present, no Device Manager problem), but the driver error history could not be.", driverLines);
        if (present.Length == 0)
            return new(spec, new(id, title, CheckDomain.HardwareDriverFirmware, CheckStatus.NotApplicable, $"No {spec.Noun} hardware reported by Windows.", [], limits.Count == 0 ? null : string.Join(" ", limits)), driverState, coverage);
        return Outcome(CheckStatus.NotFound, $"{present.Length} {spec.Noun}(s) present{(disabled > 0 ? $" ({disabled} disabled)" : string.Empty)}; no Device Manager problem, driver errors or resets in the last {inputs.WindowDays} days.", driverLines);
    }

    // "matches <baseline>", "changed X → Y since <baseline>", or why the driver was not compared.
    private static string Comparison(TriageInputs inputs, DriverRecord driver, Dictionary<string, DriverRecord> baseline, string? label)
        => inputs.Drivers is { Complete: false } ? "not compared (driver inventory incomplete)"
            : inputs.Baseline is null ? "no baseline to compare"
            : baseline.TryGetValue(driver.DeviceId, out var was)
                ? string.Equals(was.Version, driver.Version, StringComparison.OrdinalIgnoreCase) ? $"matches {label}" : $"changed {was.Version} → {driver.Version} since {label}"
                : $"not in {label}";

    private static string DriverLine(string prefix, string device, DriverRecord driver, string comparison)
        => $"{prefix} · {device}: {driver.Provider} {driver.Version}" + (driver.Date is { } date ? string.Create(CultureInfo.InvariantCulture, $" ({date:yyyy-MM-dd})") : string.Empty)
            + $", {(driver.IsSigned == false ? "NOT SIGNED" : driver.Signer is { } signer ? $"signed by {signer}" : "signer not reported")} · {comparison}.";

    private static DriverState DriverStateOf(TriageInputs inputs, List<string> changes, string? label)
        => inputs.Drivers is null ? new(false, changes, "the driver inventory could not be read")
            : !inputs.Drivers.Complete ? new(false, changes, "the driver inventory was incomplete")
            : new(inputs.Baseline is not null, changes, null, label);

    private static string ComparisonSentence(DriverState drivers)
        => drivers.Unavailable is { } why ? $" The driver version was not compared because {why}."
            : !drivers.Compared ? " No baseline was available to compare the driver version."
            : drivers.Changes.Count > 0 ? $" The driver changed ({string.Join("; ", drivers.Changes)}), and no errors were recorded in the window."
            : $" The driver matches {drivers.Baseline}.";

    // The graphics adapters' drivers against the baseline, for the graphics update note.
    private static DriverState AdapterDrivers(TriageInputs inputs, DeviceContext context)
    {
        var changes = (inputs.Drivers?.Drivers ?? []).Where(driver => driver.Class.Equals("Display", StringComparison.OrdinalIgnoreCase) && driver.DeviceId.StartsWith(@"PCI\", StringComparison.OrdinalIgnoreCase))
            .Select(driver => (driver.Name, Comparison: Comparison(inputs, driver, context.Baseline, context.Label))).Where(item => item.Comparison.StartsWith("changed", StringComparison.Ordinal))
            .Select(item => $"{item.Name}: {item.Comparison}").ToList();
        return DriverStateOf(inputs, changes, context.Label);
    }

    // Identical lines (four "HID Keyboard Device" drivers) become one line with a count, so the list stays readable in a ticket.
    private static IEnumerable<string> Collapse(IEnumerable<string> lines)
        => lines.GroupBy(line => line, StringComparer.Ordinal).Select(group => group.Count() == 1 ? group.Key : $"{group.Key.TrimEnd('.')} (×{group.Count()}).");

    // "Intel(R) Wi-Fi 6E AX210 160MHz #2" is reported by NDIS without the instance suffix.
    private static string BaseName(string name) => InstanceSuffix().Replace(name, string.Empty);

    private static string Sentence(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..] + ".";

    private static string? Coverage(string name, LogCoverage log, DateTimeOffset cutoff, TriageInputs inputs)
    {
        if (log.OldestRecord is null) return $"{name} holds no records, so nothing in the window could be examined.";
        if (log.OldestRecord <= cutoff) return log.CapReached ? $"{name}: the scan cap was reached, so only the newest records were read." : null;
        var days = Math.Max(0, (inputs.Now - log.OldestRecord.Value).TotalDays);
        return string.Create(CultureInfo.InvariantCulture, $"{name} only holds records from {Stamp(log.OldestRecord.Value)}, so the last {days:F0} of {inputs.WindowDays} days were examined.");
    }

    // ---- Windows settings (never the verdict) ---------------------------------------------------------------------------

    private static TriageCheck NetworkSettings(TriageInputs inputs)
    {
        const string id = "settings.network", title = "Wi-Fi and network adapter settings";
        const string limit = "These are Windows settings, not faults, and they never change the hardware verdict. Network connectivity, Wi-Fi signal, DNS and VPN are not checked.";
        if (inputs.Devices is null && inputs.Settings is null)
            return new(id, title, CheckDomain.DeviceSettings, CheckStatus.CouldNotCheck, "Neither the device list nor Windows' radio settings could be read.", []);
        var spec = DeviceCatalog.Spec(DeviceClass.Network);
        var adapters = inputs.Devices?.Devices.Where(device => device.Class == DeviceClass.Network && device.Present == true && spec.IsHardware(device.InstanceId)).ToArray() ?? [];
        var wireless = adapters.Any(device => DeviceCatalog.IsWireless(device.Name)) || inputs.Settings?.WifiRadios is { Count: > 0 };
        var blocking = new List<string>();
        var noted = new List<string>();
        var unread = new List<string>();
        var settings = inputs.Settings;

        if (inputs.Devices is null) unread.Add("disabled adapters");
        DisabledLines(adapters, device => DeviceCatalog.IsWireless(device.Name), blocking, noted);

        // Wi-Fi can be turned back on inside airplane mode; when every radio reads on, airplane mode is not what blocks it (as for Bluetooth).
        var wifiBackOn = settings?.WifiRadios is { Count: > 0 } on && on.All(radio => radio.Software == RadioSwitch.On);
        if (settings?.AirplaneMode is true && !wifiBackOn) blocking.Add("Airplane mode is on, which turns off Wi-Fi and Bluetooth.");
        else if (settings?.AirplaneMode is null) unread.Add("airplane mode");

        if (wireless)
        {
            var wlan = settings?.Service("WlanSvc");
            if (settings?.Services is null) unread.Add("the WLAN AutoConfig service");
            else if (wlan is { Installed: false }) blocking.Add("The WLAN AutoConfig service is not installed, so Windows cannot use a Wi-Fi adapter.");
            else if (wlan is { Running: false }) blocking.Add($"The WLAN AutoConfig service is {wlan.State?.ToLowerInvariant() ?? "not running"} (start type {wlan.StartMode ?? "unknown"}); Windows cannot use the Wi-Fi adapter without it.");

            // An empty interface list means Native Wifi saw no adapter it could report on, so the radio state was not read.
            if (settings?.WifiRadios is { Count: > 0 } radios)
                foreach (var radio in radios)
                {
                    if (radio.Hardware == RadioSwitch.Off) blocking.Add($"{radio.Adapter}: the radio is switched off by a hardware switch or function key.");
                    else if (radio.Software == RadioSwitch.Off && settings.AirplaneMode is not true) blocking.Add($"{radio.Adapter}: Wi-Fi is turned off in Windows.");
                    else if (radio.Software == RadioSwitch.Unknown && radio.Hardware == RadioSwitch.Unknown) unread.Add($"the radio state of {radio.Adapter}");
                }
            else if (wlan is null || wlan.Running) unread.Add("the Wi-Fi radio state");
        }

        return SettingsCheck(id, title, "Wi-Fi or networking", limit, blocking, noted, unread, adapters.Length == 0 ? "No network adapter hardware reported by Windows." : null,
            wireless ? "Airplane mode is off, the Wi-Fi radio is on, WLAN AutoConfig is running, and no network adapter is disabled." : "Airplane mode is off and no network adapter is disabled.");
    }

    private static TriageCheck BluetoothSettings(TriageInputs inputs)
    {
        const string id = "settings.bluetooth", title = "Bluetooth settings";
        const string limit = "These are Windows settings, not faults, and they never change the hardware verdict. Pairing and a paired device's own state are not checked.";
        if (inputs.Devices is null && inputs.Settings is null)
            return new(id, title, CheckDomain.DeviceSettings, CheckStatus.CouldNotCheck, "Neither the device list nor Windows' radio settings could be read.", []);
        var radios = PresentHardware(inputs, DeviceClass.Bluetooth);
        var settings = inputs.Settings;
        var blocking = new List<string>();
        var noted = new List<string>();
        var unread = new List<string>();

        if (inputs.Devices is null) unread.Add("disabled radios");
        DisabledLines(radios, _ => true, blocking, noted);
        // Without a radio, airplane mode and the Bluetooth toggle cannot be what blocks it; the device check says the radio is missing.
        if (radios.Length > 0 || inputs.Devices is null)
        {
            if (settings?.BluetoothDisallowedByPolicy is true) blocking.Add("Device-management policy (Connectivity/AllowBluetooth = 0) disallows Bluetooth on this computer.");
            else if (settings?.BluetoothDisallowedByPolicy is null) unread.Add("the Bluetooth policy");
            if (settings?.AirplaneMode is true) blocking.Add("Airplane mode is on, which turns off Bluetooth unless it was turned back on.");
            if (settings?.BluetoothRadios is { } states)
                foreach (var radio in states)
                {
                    if (radio.Hardware == RadioSwitch.Off) blocking.Add($"{radio.Adapter}: the radio is held off by firmware or a hardware switch and cannot be turned on from Windows.");
                    else if (radio.Software == RadioSwitch.Off) blocking.Add($"{radio.Adapter}: Bluetooth is turned off in Windows.");
                }
            else unread.Add("the Bluetooth radio state");
            // The Bluetooth Support Service starts on demand, so "stopped" alone is normal; a disabled service is not.
            var service = settings?.Service("bthserv");
            if (settings?.Services is null) unread.Add("the Bluetooth Support Service");
            else if (service is { Installed: true } && string.Equals(service.StartMode, "Disabled", StringComparison.OrdinalIgnoreCase))
                blocking.Add("The Bluetooth Support Service (bthserv) is disabled, so Windows cannot discover or pair Bluetooth devices.");
        }
        // Airplane mode with the radio on means the user turned Bluetooth back on; drop the airplane line in that case.
        if (settings?.BluetoothRadios is { Count: > 0 } on && on.All(radio => radio.Software == RadioSwitch.On))
            blocking.RemoveAll(line => line.StartsWith("Airplane mode", StringComparison.Ordinal));

        return SettingsCheck(id, title, "Bluetooth", limit, blocking, noted, unread, radios.Length == 0 ? "No Bluetooth radio hardware reported by Windows." : null,
            "Bluetooth is on, not disallowed by policy, and the Bluetooth Support Service is not disabled.");
    }

    private static TriageCheck AudioSettings(TriageInputs inputs)
    {
        const string id = "settings.audio", title = "Sound and microphone settings";
        const string limit = "These are Windows settings, not faults, and they never change the hardware verdict. Volume inside an application (Teams, Zoom, a browser tab) and Bluetooth headset profiles are not read.";
        if (inputs.Settings is not { } settings)
            return new(id, title, CheckDomain.DeviceSettings, CheckStatus.CouldNotCheck, "Windows' sound settings could not be read.", []);
        var blocking = new List<string>();
        var noted = new List<string>();
        var unread = new List<string>();

        foreach (var (name, label) in new[] { ("Audiosrv", "Windows Audio"), ("AudioEndpointBuilder", "Windows Audio Endpoint Builder") })
        {
            var service = settings.Service(name);
            if (settings.Services is null) { unread.Add("the audio services"); break; }
            if (service is { Installed: true, Running: false })
                blocking.Add($"The {label} service is {service.State?.ToLowerInvariant() ?? "not running"} (start type {service.StartMode ?? "unknown"}); no sound device works without it.");
        }

        if (settings.Audio is { } audio)
        {
            if (audio.ActivePlayback == 0 && audio.DisabledPlayback > 0) blocking.Add($"No playback device is active; {audio.DisabledPlayback} are disabled in Sound settings.");
            else if (audio.ActivePlayback == 0 && audio.UnpluggedPlayback > 0) blocking.Add($"No playback device is active; {audio.UnpluggedPlayback} report nothing plugged in (speakers or headphones not connected to the jack).");
            else if (audio.ActivePlayback == 0) noted.Add("Windows lists no playback device at all; see Audio devices for the hardware.");
            if (audio.DefaultPlayback is { Muted: true } speaker) blocking.Add($"The default playback device ({speaker.Name}) is muted.");
            else if (audio.DefaultPlayback is { Volume: < 0.01 } quiet) blocking.Add($"The default playback device ({quiet.Name}) is at 0% volume.");
            if (audio.DefaultRecording is { Muted: true } microphone) blocking.Add($"The default recording device ({microphone.Name}) is muted.");
            if (audio.ActiveRecording == 0 && audio.DisabledRecording > 0) noted.Add($"No recording device is active; {audio.DisabledRecording} are disabled in Sound settings.");
            if (audio.DefaultPlayback is { } playback && (playback.Muted is null || playback.Volume is null)) unread.Add($"the mute and volume of {playback.Name}");
        }
        else unread.Add((settings.RemoteSession ?? settings.SessionProblem) is { } why ? $"the sound devices ({why.TrimEnd('.')})" : "the sound devices");
        PrivacyLines(settings.Microphone, "microphone", settings.SessionProblem, blocking, unread);

        return SettingsCheck(id, title, "sound or the microphone", limit, blocking, noted, unread, notApplicable: null,
            "Windows Audio is running, a playback device is active, the default devices are not muted or at 0%, and microphone privacy allows the supported apps.");
    }

    private static DeviceRecord[] PresentHardware(TriageInputs inputs, DeviceClass deviceClass)
        => inputs.Devices?.Devices.Where(device => device.Class == deviceClass && device.Present == true && DeviceCatalog.Spec(deviceClass).IsHardware(device.InstanceId)).ToArray() ?? [];

    // A disabled device blocks only when no other enabled device of the same kind is there to take over.
    private static void DisabledLines<TKind>(DeviceRecord[] devices, Func<DeviceRecord, TKind> kind, List<string> blocking, List<string> noted)
    {
        foreach (var device in devices.Where(device => device.Disabled))
        {
            var other = devices.FirstOrDefault(item => !item.Disabled && EqualityComparer<TKind>.Default.Equals(kind(item), kind(device)));
            if (other is null) blocking.Add($"{device.Name} is disabled in Device Manager.");
            else noted.Add($"{device.Name} is disabled in Device Manager; {other.Name} is enabled.");
        }
    }

    private static TriageCheck CameraSettings(TriageInputs inputs)
    {
        const string id = "settings.camera", title = "Camera settings";
        const string limit = "These are Windows settings, not faults, and they never change the hardware verdict. Site permissions inside Chrome or Edge (Google Meet) and settings inside Teams or Zoom are not read.";
        var cameras = PresentHardware(inputs, DeviceClass.Camera);
        if (inputs.Devices is not null && cameras.Length == 0)
            return new(id, title, CheckDomain.DeviceSettings, CheckStatus.NotApplicable, "No camera reported by Windows.", []);
        if (inputs.Settings is not { } settings)
            return new(id, title, CheckDomain.DeviceSettings, CheckStatus.CouldNotCheck, "Windows' camera settings could not be read.", []);
        var blocking = new List<string>();
        var noted = new List<string>();
        var unread = new List<string>();
        if (inputs.Devices is null) unread.Add("disabled cameras");
        DisabledLines(cameras, _ => true, blocking, noted);
        PrivacyLines(settings.Camera, "camera", settings.SessionProblem, blocking, unread);
        if (settings.Services is null) unread.Add("the Windows Camera Frame Server service");
        else if (settings.Service("FrameServer") is { Installed: true } frame && string.Equals(frame.StartMode, "Disabled", StringComparison.OrdinalIgnoreCase))
            blocking.Add("The Windows Camera Frame Server service is disabled, so Teams, Zoom and the Camera app cannot open the camera.");
        if (settings.Camera?.InUseBy is { Count: > 0 } users)
            noted.Add($"Windows records the camera as in use right now by {string.Join(", ", users)}; another app may not be able to open it until that one lets go.");
        return SettingsCheck(id, title, "the camera", limit, blocking, noted, unread, null,
            "Camera access is allowed for this device, the signed-in user, desktop apps (Zoom, Google Meet in a browser) and the new Teams and Camera apps, and no app is recorded as using it now.");
    }

    private static TriageCheck DisplaySettings(TriageInputs inputs)
    {
        const string id = "settings.display", title = "Display settings";
        const string limit = "These are Windows settings, not faults, and they never change the hardware verdict. Brightness, refresh rate, and the monitor's own power and input selection are not read.";
        var monitors = PresentHardware(inputs, DeviceClass.Display);
        if (inputs.Devices is not null && monitors.Length == 0)
            return new(id, title, CheckDomain.DeviceSettings, CheckStatus.NotApplicable, "No monitor reported by Windows.", []);
        var blocking = new List<string>();
        var noted = new List<string>();
        var unread = new List<string>();
        DisabledLines(monitors, device => device.BuiltIn, blocking, noted);
        var connected = monitors.Count(device => !device.Disabled);
        if (inputs.Settings?.Display is { } layout)
        {
            if (layout.Topology == DisplayTopology.PcScreenOnly && connected >= 2)
                blocking.Add($"Windows is set to \"PC screen only\" (Win+P), so {connected - 1} other connected monitor(s) stay dark.");
            else if (layout.Topology == DisplayTopology.SecondScreenOnly && monitors.Any(device => device.BuiltIn && !device.Disabled))
                noted.Add("Windows is set to \"Second screen only\" (Win+P), so the built-in screen stays dark.");
            else if (layout.ActiveDisplays < connected)
                noted.Add($"{connected} monitors are connected but Windows is drawing on {layout.ActiveDisplays}. A monitor can be turned off in Display settings (\"Disconnect this display\"), or a laptop lid may be closed.");
        }
        else unread.Add((inputs.Settings?.RemoteSession ?? inputs.Settings?.SessionProblem) is { } why ? $"the display layout ({why.TrimEnd('.')})" : "the display layout");
        return SettingsCheck(id, title, "a display", limit, blocking, noted, unread, null, $"Every connected monitor ({connected}) is in use and none is disabled.");
    }

    private static TriageCheck InputSettings(TriageInputs inputs)
    {
        const string id = "settings.input", title = "Keyboard, mouse and touchpad settings";
        const string limit = "These are Windows settings, not faults, and they never change the hardware verdict. Keyboard layout and language, and settings in a touchpad vendor's own app, are not read.";
        var devices = PresentHardware(inputs, DeviceClass.Input);
        if (inputs.Devices is not null && devices.Length == 0)
            return new(id, title, CheckDomain.DeviceSettings, CheckStatus.NotApplicable, "No keyboard, mouse or touchpad reported by Windows.", []);
        var blocking = new List<string>();
        var noted = new List<string>();
        var unread = new List<string>();
        DisabledLines(devices, device => device.ClassName, blocking, noted);
        // A built-in pointing device (or an I2C HID device) is the touchpad; an external one in the Mouse class is a mouse.
        var touchpad = devices.Any(device => device.BuiltIn && (device.ClassName.Equals("Mouse", StringComparison.OrdinalIgnoreCase) || string.Equals(device.Service, "hidi2c", StringComparison.OrdinalIgnoreCase)));
        var mouse = devices.Any(device => !device.BuiltIn && device.ClassName.Equals("Mouse", StringComparison.OrdinalIgnoreCase) && !device.Disabled);
        if (inputs.Settings?.Input is { } input)
        {
            if (input.FilterKeys == true) blocking.Add("Filter Keys is on, so short or repeated key presses are ignored and keys can seem dead.");
            if (input.MouseKeys == true) blocking.Add("Mouse Keys is on, so the numeric keypad moves the pointer instead of typing numbers.");
            if (input.FilterKeys is null || input.MouseKeys is null) unread.Add("the accessibility key settings");
            if (touchpad && input.TouchpadEnabled == false) blocking.Add("The touchpad is turned off in Settings.");
            else if (touchpad && mouse && input.TouchpadOffWithMouse == true) blocking.Add("The touchpad is set to turn off while a mouse is connected, and a mouse is connected.");
        }
        else unread.Add(inputs.Settings?.SessionProblem is { } why ? $"the keyboard and touchpad settings ({why.TrimEnd('.')})" : "the keyboard and touchpad settings");
        return SettingsCheck(id, title, "the keyboard, mouse or touchpad", limit, blocking, noted, unread, null,
            "Filter Keys and Mouse Keys are off, the touchpad (if any) is on, and no keyboard, mouse or touchpad is disabled.");
    }

    // Privacy switches for one capability, phrased for the apps this team supports: new Teams and the Camera app are Store apps;
    // Zoom, Google Meet (in Chrome or Edge) and classic Teams are desktop apps.
    private static void PrivacyLines(PrivacyReading? privacy, string noun, string? sessionProblem, List<string> blocking, List<string> unread)
    {
        if (privacy is null) { unread.Add($"the {noun} privacy settings"); return; }
        if (privacy.DisabledByPolicy) blocking.Add($"Group Policy (Allow use of camera = 0) turns the {noun} off for every app.");
        if (privacy.DeviceDenied == true) blocking.Add($"{char.ToUpperInvariant(noun[0])}{noun[1..]} access is turned off for this device in Privacy settings, which blocks every app.");
        if (privacy.AppPolicy == 2) blocking.Add($"Group Policy denies Store apps (including the new Microsoft Teams and the Camera app) access to the {noun}.");
        else if (privacy.PolicyDeniedApps.Count > 0) blocking.Add($"Group Policy denies {string.Join(", ", privacy.PolicyDeniedApps)} access to the {noun}.");
        if (privacy.UserDenied is null) { unread.Add($"the signed-in user's {noun} privacy switches ({sessionProblem ?? "unreadable"})"); return; }
        if (privacy.UserDenied == true) blocking.Add($"\"Let apps access your {noun}\" is off in Privacy settings.");
        if (privacy.DesktopAppsDenied == true) blocking.Add($"\"Let desktop apps access your {noun}\" is off, which blocks Zoom, Google Meet in a browser, and classic Teams.");
        foreach (var app in privacy.DeniedApps ?? []) blocking.Add($"{app} is not allowed to use the {noun} in Privacy settings.");
    }

    // `blocks` is what the setting stands between the user and ("Wi-Fi or networking"), carried on the check for the hand-off headline.
    private static TriageCheck SettingsCheck(string id, string title, string blocks, string limit, List<string> blocking, List<string> noted, List<string> unread, string? notApplicable, string clean)
    {
        var unreadNote = unread.Count == 0 ? null : $"Could not read {string.Join("; ", unread.Distinct().Select(item => item.TrimEnd('.')))}.";
        if (blocking.Count + noted.Count > 0)
        {
            var lines = blocking.Concat(noted).ToArray();
            return new(id, title, CheckDomain.DeviceSettings, CheckStatus.Found, lines[0] + (lines.Length > 1 ? $" (+{lines.Length - 1} more)" : string.Empty), lines.Length > 1 ? lines : [],
                unreadNote is null ? limit : unreadNote + " " + limit, Minor: blocking.Count == 0) { Blocks = blocks };
        }
        if (unreadNote is not null) return new(id, title, CheckDomain.DeviceSettings, CheckStatus.CouldNotCheck, unreadNote, [], limit) { Blocks = blocks };
        if (notApplicable is not null) return new(id, title, CheckDomain.DeviceSettings, CheckStatus.NotApplicable, notApplicable, []) { Blocks = blocks };
        return new(id, title, CheckDomain.DeviceSettings, CheckStatus.NotFound, clean, [], limit) { Blocks = blocks };
    }

    // ---- What the evidence says about updates -------------------------------------------------------------------------

    private static UpdateNote? DriverNote(DeviceOutcome outcome, int days)
    {
        var (spec, check) = (outcome.Spec, outcome.Check);
        var subject = $"{char.ToUpperInvariant(spec.Noun[0])}{spec.Noun[1..]} driver";
        switch (check.Status)
        {
            case CheckStatus.NotApplicable: return null;
            case CheckStatus.CouldNotCheck: return new(subject, UpdateLean.CouldNotAssess, $"Could not be assessed: {check.Summary}");
            case CheckStatus.Found when !check.Minor:
                return new(subject, UpdateLean.PointsHere, $"Windows recorded evidence at the {spec.Noun} or its driver: {check.Summary} The {check.Title} check lists the records; they show where to look, not which fix applies.");
        }
        var window = outcome.Coverage is null ? $"in the last {days} days" : "in the part of the logs that was read";
        var basis = check.Status == CheckStatus.Found
            ? $"Only minor signals were recorded ({check.Summary.TrimEnd('.')}); on their own they do not point to the driver."
            : $"Nothing in this scan points to the {spec.Noun} driver: no Device Manager problem, driver errors or resets {window}.";
        return new(subject, UpdateLean.NothingPointsHere, basis + ComparisonSentence(outcome.Drivers) + " This evidence does not support a driver update.");
    }

    private static UpdateNote BiosNote(TriageInputs inputs, IReadOnlyList<TriageCheck> checks, bool hardwareLeanStop)
    {
        const string subject = "BIOS / firmware";
        var relevant = checks.Where(check => check.Id is "whea" or "firmware" or "crashes").ToArray();
        var change = inputs.Baseline?.BiosVersion is { } before && inputs.BiosVersion is { } now && !before.Equals(now, StringComparison.OrdinalIgnoreCase)
            ? $" The BIOS changed since the baseline ({before} → {now})." : string.Empty;
        var skipped = relevant.Where(check => check.Status == CheckStatus.CouldNotCheck).Select(check => check.Title).ToArray();
        if (skipped.Length > 0) return new(subject, UpdateLean.CouldNotAssess, $"Could not be assessed: {string.Join(", ", skipped)} could not be checked.{change}");
        var points = relevant.Where(check => check.Id is "whea" or "firmware" && check.Status == CheckStatus.Found && !check.Minor).Select(check => check.Title).ToList();
        if (hardwareLeanStop) points.Add("hardware-leaning stop codes");
        if (points.Count > 0)
            return new(subject, UpdateLean.PointsHere, $"Windows recorded evidence that can involve the platform or its firmware: {string.Join("; ", points)}. These records say where an error surfaced, not that the BIOS is at fault.{change}");
        var minor = relevant.Where(check => check.Id is "whea" or "firmware" && check.Status == CheckStatus.Found).Select(check => check.Title).ToArray();
        var age = inputs.BiosReleaseDate is { } released ? $" BIOS age ({(int)((inputs.Now - released).TotalDays / 30.4)} months) is not by itself evidence of a fault." : string.Empty;
        return new(subject, UpdateLean.NothingPointsHere,
            $"Nothing in this scan points to the BIOS: no significant WHEA hardware errors, firmware processor limits or hardware-leaning stop codes in the last {inputs.WindowDays} days."
            + (minor.Length > 0 ? $" Minor signals only ({string.Join("; ", minor)})." : string.Empty) + age + change + " This evidence does not support a BIOS update.");
    }

    private static UpdateNote? GraphicsNote(TriageCheck? graphics, DriverState adapters) => graphics?.Status switch
    {
        CheckStatus.CouldNotCheck => new("Graphics driver", UpdateLean.CouldNotAssess, $"Could not be assessed: {graphics.Summary}"),
        CheckStatus.Found when !graphics.Minor => new("Graphics driver", UpdateLean.PointsHere, $"Windows recorded evidence at the graphics driver or GPU: {graphics.Summary} Driver resets point to the driver or its settings first."),
        CheckStatus.Found => new("Graphics driver", UpdateLean.NothingPointsHere,
            $"Only minor signals were recorded ({graphics.Summary.TrimEnd('.')}); on their own they do not point to the driver.{ComparisonSentence(adapters)} This evidence does not support a driver update."),
        CheckStatus.NotFound => new("Graphics driver", UpdateLean.NothingPointsHere,
            $"Nothing in this scan points to the graphics driver: no display driver resets, GPU hangs or graphics-leaning crashes.{ComparisonSentence(adapters)} This evidence does not support a driver update."),
        _ => null
    };

    [GeneratedRegex(@"^Netwtw\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NetwtwProvider();

    [GeneratedRegex(@"Reason:\s*(.+?)(?:\.\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex ResetReason();

    [GeneratedRegex(@"\s+#\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex InstanceSuffix();
}
