using Skald.Core.Models;
using Skald.Triage;
using Xunit;

namespace Skald.Core.Tests;

public sealed class DeviceTriageTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private const string Wifi = @"PCI\VEN_8086&DEV_2725&SUBSYS_00248086&REV_1A\4&1A2B3C4D&0&00E0";
    private const string WifiName = "Intel(R) Wi-Fi 6E AX210 160MHz";
    private const string Ethernet = @"PCI\VEN_10EC&DEV_8125&SUBSYS_87D71043&REV_05\01000000684CE00000";
    private const string EthernetName = "Realtek Gaming 2.5GbE Family Controller";
    private const string PnpLog = "Microsoft-Windows-Kernel-PnP/Configuration";

    private static DeviceRecord Adapter(string id = Wifi, string name = WifiName, uint? code = 0, bool? present = true, string service = "Netwtw10", bool builtIn = true)
        => new(id, name, DeviceClass.Network, "Net", present, code, service, builtIn);

    private static DriverRecord Driver(string id = Wifi, string version = "23.60.1.2", bool signed = true, string name = WifiName)
        => new(id, name, "NET", version, Now.AddDays(-200), "Intel", "oem12.inf", signed, signed ? "Microsoft Windows Hardware Compatibility Publisher" : null);

    private static ReliabilityEvent Event(string provider, int id, string severity = "Warning", string details = "", string raw = "", string log = "System", double daysAgo = 1)
        => new(Now.AddDays(-daysAgo), log, Random.Shared.NextInt64(1, long.MaxValue), provider, id, "Devices", severity, provider, details.Length > 0 ? details : "Summary", details, raw);

    private static string PnpData(string instance, string extra = "")
        => $"<Event><EventData><Data Name='DeviceInstanceId'>{instance.Replace("&", "&amp;", StringComparison.Ordinal)}</Data>{extra}</EventData></Event>";

    private sealed class Inputs
    {
        public List<DeviceRecord> Devices { get; } = [Adapter()];
        public List<ReliabilityEvent> DeviceEvents { get; } = [];
        public List<ReliabilityEvent> SystemEvents { get; } = [];
        public List<LogCoverage> Logs { get; } = [new("System", true, Now.AddDays(-60)), new(PnpLog, true, Now.AddDays(-60))];
        public HashSet<DeviceClass> Unreadable { get; } = [];
        public bool DeviceEvidence { get; set; } = true;
        public DriverInventory? Drivers { get; set; } = new(Now, [Driver()], []);
        public DriverSnapshot? Baseline { get; set; } = new(Now.AddDays(-7), "PC", null, "1.0", "25H2", [Driver()]);
        public DeviceSettings? Settings { get; set; } = new(Now, []) { AirplaneMode = false, Services = [new("WlanSvc", true, "Running", "Auto")], WifiRadios = [new(WifiName, RadioSwitch.On, RadioSwitch.On)] };
        public SystemInventory System { get; } = new() { DevicesAvailable = true, StorageAvailable = true };
        public string[] ReliabilityLogs { get; set; } = ["System: 100 records scanned", "Application: 50 records scanned"];
        public ReliabilityEvent[] Whea { get; set; } = [];

        public TriageReport Run() => TriageEngine.Evaluate(new TriageInputs(Now, 30, false)
        {
            Reliability = new(Now, SystemEvents.Concat(Whea).ToArray(), ReliabilityLogs),
            System = System, Drivers = Drivers, Baseline = Baseline, BaselineLabel = Baseline is null ? null : "golden.json", Settings = Settings,
            Devices = DeviceEvidence ? new DeviceEvidence(Now, Devices, DeviceEvents, []) { Logs = Logs, UnreadableClasses = Unreadable } : null
        }, "test");
    }

    private static TriageCheck Check(TriageReport report, string id) => report.Checks.Single(check => check.Id == id);
    private static UpdateNote Note(TriageReport report, string subject) => report.UpdateEvidence.Single(note => note.Subject == subject);
    private static string All(TriageCheck check) => string.Join('\n', check.Details.Append(check.Summary).Append(check.Limit ?? string.Empty));

    // ---- Missing or unreadable evidence never yields "nothing found" --------------------------------------------------

    [Fact]
    public void UnreadableDeviceListIsCouldNotCheckNotNothingFound()
    {
        var report = new Inputs { DeviceEvidence = false, Settings = null }.Run();
        Assert.Equal(CheckStatus.CouldNotCheck, Check(report, "devices.network").Status);
        Assert.Equal(CheckStatus.CouldNotCheck, Check(report, "settings.network").Status);
        Assert.Equal(UpdateLean.CouldNotAssess, Note(report, "Network adapter driver").Lean);
        Assert.Contains("could not run", report.Verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void PartlyReadDeviceClassIsCouldNotCheck()
    {
        var inputs = new Inputs();
        inputs.Unreadable.Add(DeviceClass.Network);
        Assert.Equal(CheckStatus.CouldNotCheck, Check(inputs.Run(), "devices.network").Status);
    }

    [Fact]
    public void HealthyDeviceWithUnreadableSystemLogIsCouldNotCheck()
    {
        var inputs = new Inputs();
        inputs.Logs[0] = new("System", false, null);
        var report = inputs.Run();
        var check = Check(report, "devices.network");
        Assert.Equal(CheckStatus.CouldNotCheck, check.Status);
        Assert.Contains("driver error history could not be", check.Summary, StringComparison.Ordinal);
        Assert.Equal(UpdateLean.CouldNotAssess, Note(report, "Network adapter driver").Lean);
    }

    [Fact]
    public void ProblemCodeStillCountsWhenTheSystemLogIsUnreadable()
    {
        var inputs = new Inputs();
        inputs.Logs[0] = new("System", false, null);
        inputs.Devices[0] = Adapter(code: 10);
        var check = Check(inputs.Run(), "devices.network");
        Assert.Equal(CheckStatus.Found, check.Status);
        Assert.Contains("System event log could not be read", check.Limit, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutABaselineTheDriverIsNeverSaidToMatch()
    {
        var note = Note(new Inputs { Baseline = null }.Run(), "Network adapter driver");
        Assert.Equal(UpdateLean.NothingPointsHere, note.Lean);
        Assert.Contains("No baseline", note.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("matches", note.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void IncompleteDriverInventoryIsNotCompared()
    {
        var report = new Inputs { Drivers = new DriverInventory(Now, [Driver()], []) { Complete = false } }.Run();
        Assert.Contains("not compared", All(Check(report, "devices.network")), StringComparison.Ordinal);
        Assert.Contains("was not compared", Note(report, "Network adapter driver").Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ShortSystemLogIsDisclosedOnACleanResult()
    {
        var inputs = new Inputs();
        inputs.Logs[0] = new("System", true, Now.AddDays(-5));
        var report = inputs.Run();
        Assert.Equal(CheckStatus.NotFound, Check(report, "devices.network").Status);
        Assert.Contains("only holds records from", Check(report, "devices.network").Limit, StringComparison.Ordinal);
        Assert.Contains("in the part of the logs that was read", Note(report, "Network adapter driver").Text, StringComparison.Ordinal);
    }

    [Fact]
    public void UnreadableRadioStateWithNothingElseFoundIsCouldNotCheck()
    {
        var report = new Inputs { Settings = new DeviceSettings(Now, []) { AirplaneMode = false, Services = [new("WlanSvc", true, "Running", "Auto")] } }.Run();
        Assert.Equal(CheckStatus.CouldNotCheck, Check(report, "settings.network").Status);
        Assert.Contains("Wi-Fi radio state", Check(report, "settings.network").Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void UnreadableAirplaneModeIsCouldNotCheck()
        => Assert.Equal(CheckStatus.CouldNotCheck, Check(new Inputs { Settings = new DeviceSettings(Now, []) { Services = [new("WlanSvc", true, "Running", "Auto")], WifiRadios = [new(WifiName, RadioSwitch.On, RadioSwitch.On)] } }.Run(), "settings.network").Status);

    // ---- Clean results say what they rest on ---------------------------------------------------------------------------

    [Fact]
    public void HealthyAdapterMatchingTheBaselineSaysNothingPointsToTheDriver()
    {
        var report = new Inputs().Run();
        var check = Check(report, "devices.network");
        Assert.Equal(CheckStatus.NotFound, check.Status);
        Assert.Contains("signed by Microsoft Windows Hardware Compatibility Publisher", All(check), StringComparison.Ordinal);
        Assert.Contains("matches golden.json", All(check), StringComparison.Ordinal);
        var note = Note(report, "Network adapter driver");
        Assert.Equal(UpdateLean.NothingPointsHere, note.Lean);
        Assert.Contains("does not support a driver update", note.Text, StringComparison.Ordinal);
        Assert.Equal(CheckStatus.NotFound, Check(report, "settings.network").Status);
        Assert.Equal(TriageOutcome.NoHardwareEvidence, report.Verdict.Outcome);
    }

    [Fact]
    public void ChangedDriverIsADetailNotEvidence()
    {
        var report = new Inputs { Baseline = new DriverSnapshot(Now.AddDays(-7), "PC", null, "1.0", "25H2", [Driver(version: "22.0.0.1")]) }.Run();
        var check = Check(report, "devices.network");
        Assert.Equal(CheckStatus.NotFound, check.Status);
        Assert.Contains("changed 22.0.0.1 → 23.60.1.2", All(check), StringComparison.Ordinal);
        Assert.Contains("The driver changed", Note(report, "Network adapter driver").Text, StringComparison.Ordinal);
    }

    [Fact]
    public void NoNetworkHardwareIsNotApplicable()
    {
        var inputs = new Inputs { Baseline = null };
        inputs.Devices.Clear();
        inputs.Devices.Add(new(@"SWD\MSRRAS\MS_PPTPMINIPORT", "WAN Miniport (PPTP)", DeviceClass.Network, "Net", true, 0, "PptpMiniport", false));
        var report = inputs.Run();
        Assert.Equal(CheckStatus.NotApplicable, Check(report, "devices.network").Status);
        Assert.DoesNotContain(report.UpdateEvidence, note => note.Subject == "Network adapter driver");
    }

    // ---- Present and enumerating ---------------------------------------------------------------------------------------

    [Fact]
    public void BuiltInAdapterInTheBaselineThatStoppedEnumeratingIsSignificant()
    {
        var inputs = new Inputs();
        inputs.Devices[0] = Adapter(present: false, code: null);
        var report = inputs.Run();
        var check = Check(report, "devices.network");
        Assert.Equal(CheckStatus.Found, check.Status);
        Assert.False(check.Minor);
        Assert.Contains("stopped enumerating", All(check), StringComparison.Ordinal);
        Assert.Equal(TriageOutcome.HardwareEvidenceFound, report.Verdict.Outcome);
        Assert.Equal(UpdateLean.PointsHere, Note(report, "Network adapter driver").Lean);
    }

    [Fact]
    public void InternalAdapterInTheBaselineThatWindowsNoLongerListsIsSignificant()
    {
        var inputs = new Inputs();
        inputs.Devices.Clear();
        var check = Check(inputs.Run(), "devices.network");
        Assert.Equal(CheckStatus.Found, check.Status);
        Assert.Contains("no longer lists it at all", All(check), StringComparison.Ordinal);
    }

    [Fact]
    public void UnpluggedExternalAdapterIsNotEvidence()
    {
        const string dongle = @"USB\VID_0BDA&PID_8153\000001";
        var inputs = new Inputs { Baseline = new DriverSnapshot(Now.AddDays(-7), "PC", null, "1.0", "25H2", [Driver(), Driver(dongle, name: "USB GbE")]) };
        inputs.Devices.Add(new(dongle, "USB GbE", DeviceClass.Network, "Net", false, null, "rtux64w10", false));
        var check = Check(inputs.Run(), "devices.network");
        Assert.Equal(CheckStatus.NotFound, check.Status);
        Assert.Contains("not connected now", check.Limit, StringComparison.Ordinal);
    }

    [Fact]
    public void SamePartUnderANewInstanceIdHasNotDisappeared()
    {
        const string updated = @"PCI\VEN_8086&DEV_2725&SUBSYS_00248086&REV_1B\4&99&0&00E0";
        var inputs = new Inputs();
        inputs.Devices[0] = Adapter(id: updated);
        Assert.Equal(CheckStatus.NotFound, Check(inputs.Run(), "devices.network").Status);
    }

    [Fact]
    public void RememberedBuiltInAdapterWithoutABaselineIsMinor()
    {
        var inputs = new Inputs { Baseline = null };
        inputs.Devices.Add(Adapter(id: @"PCI\VEN_8086&DEV_7A70&SUBSYS_00748086&REV_11\3&11583659&0&A3", name: "Intel(R) Wi-Fi 6 AX201", present: false, code: null));
        var check = Check(inputs.Run(), "devices.network");
        Assert.Equal(CheckStatus.Found, check.Status);
        Assert.True(check.Minor);
    }

    // ---- Health, drivers and events ------------------------------------------------------------------------------------

    [Fact]
    public void ProblemCodeIsEvidenceAndIsNotCountedTwice()
    {
        var inputs = new Inputs();
        inputs.Devices[0] = Adapter(code: 10);
        inputs.System.DeviceProblems.Add(new(WifiName, 10, Wifi));
        var report = inputs.Run();
        var check = Check(report, "devices.network");
        Assert.Equal(CheckStatus.Found, check.Status);
        Assert.False(check.Minor);
        Assert.Contains("Code 10 · Cannot start", All(check), StringComparison.Ordinal);
        Assert.Equal(CheckStatus.NotFound, Check(report, "devices").Status);
        Assert.Equal("Device Manager problems (other devices)", Check(report, "devices").Title);
    }

    [Fact]
    public void UnsignedDriverIsEvidenceInTheClassCheckOnly()
    {
        var report = new Inputs { Drivers = new DriverInventory(Now, [Driver(signed: false)], []) }.Run();
        Assert.False(Check(report, "devices.network").Minor);
        Assert.Contains("NOT SIGNED", All(Check(report, "devices.network")), StringComparison.Ordinal);
        Assert.Equal(CheckStatus.NotFound, Check(report, "drivers").Status);
    }

    [Fact]
    public void RoutineIntelWiFiWarningsAreIgnoredAndDisclosed()
    {
        var inputs = new Inputs();
        for (var i = 0; i < 47; i++) inputs.DeviceEvents.Add(Event("Netwtw10", 6062, details: "6062 - Lso was triggered"));
        var check = Check(inputs.Run(), "devices.network");
        Assert.Equal(CheckStatus.NotFound, check.Status);
        Assert.Contains("47 routine driver warning(s)", check.Limit, StringComparison.Ordinal);
    }

    [Fact]
    public void DriverErrorsAreMinorUntilTheyRepeat()
    {
        var once = new Inputs();
        once.DeviceEvents.Add(Event("Netwtw10", 5002, "Error", "5002 - Firmware error"));
        Assert.True(Check(once.Run(), "devices.network").Minor);

        var repeated = new Inputs();
        for (var i = 0; i < 3; i++) repeated.DeviceEvents.Add(Event("Netwtw10", 5002, "Error", "5002 - Firmware error"));
        var check = Check(repeated.Run(), "devices.network");
        Assert.False(check.Minor);
        Assert.Contains($"Netwtw10 ({WifiName} driver) error event 5002 ×3", All(check), StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatedHardwareAdapterResetsAreEvidenceAndVirtualAdapterResetsAreIgnored()
    {
        string Reset(string adapter) => $"The network interface \"{adapter}\" has begun resetting. There will be a momentary disruption in network connectivity while the hardware resets. Reason: The network driver detected that its hardware has stopped responding to commands. This network interface has reset 3 time(s) since it was last initialized.";
        var inputs = new Inputs();
        for (var i = 0; i < 3; i++) inputs.DeviceEvents.Add(Event("Microsoft-Windows-NDIS", 10400, details: Reset(WifiName)));
        inputs.DeviceEvents.Add(Event("Microsoft-Windows-NDIS", 10400, details: Reset("Microsoft Wi-Fi Direct Virtual Adapter #2")));
        var check = Check(inputs.Run(), "devices.network");
        Assert.False(check.Minor);
        Assert.Contains($"{WifiName}: adapter reset ×3", All(check), StringComparison.Ordinal);
        Assert.Contains("hardware has stopped responding to commands", All(check), StringComparison.Ordinal);
        Assert.DoesNotContain("Virtual Adapter", All(check), StringComparison.Ordinal);
    }

    [Fact]
    public void FailedDeviceStartIsSignificantAndDriverInstallsAreListed()
    {
        var inputs = new Inputs();
        inputs.DeviceEvents.Add(Event("Microsoft-Windows-Kernel-PnP", 411, "Error", log: PnpLog, raw: PnpData(Wifi, "<Data Name='Problem'>0xa</Data>")));
        inputs.DeviceEvents.Add(Event("Microsoft-Windows-Kernel-PnP", 400, "Information", log: PnpLog,
            raw: PnpData(Wifi, "<Data Name='DriverVersion'>23.60.1.2</Data><Data Name='DriverProvider'>Intel</Data><Data Name='DeviceUpdated'>true</Data>")));
        var check = Check(inputs.Run(), "devices.network");
        Assert.False(check.Minor);
        Assert.Contains($"{WifiName}: had a problem starting ×1", All(check), StringComparison.Ordinal);
        Assert.Contains("Driver installed", All(check), StringComparison.Ordinal);
    }

    [Fact]
    public void DeviceSetupRecordsStayWithTheirOwnClass()
    {
        var report = WithAudio(inputs => inputs.DeviceEvents.Add(Event("Microsoft-Windows-Kernel-PnP", 411, "Error", log: PnpLog, raw: PnpData(Wifi, "<Data Name='Problem'>0xa</Data>")))).Run();
        Assert.Equal(CheckStatus.Found, Check(report, "devices.network").Status);
        Assert.Equal(CheckStatus.NotFound, Check(report, "devices.audio").Status);
    }

    [Fact]
    public void DriverLoadFailureMovesFromTheDriversCheckToTheClassCheck()
    {
        var inputs = new Inputs();
        inputs.SystemEvents.Add(new(Now.AddDays(-1), "System", 7, "Microsoft-Windows-Kernel-PnP", 219, "Drivers & storage", "Warning", "x", "The driver failed to load", "",
            $"<Event><EventData><Data Name='DriverName'>{Wifi.Replace("&", "&amp;", StringComparison.Ordinal)}</Data><Data Name='FailureName'>\\Driver\\Netwtw10</Data></EventData></Event>"));
        var report = inputs.Run();
        Assert.Contains("driver failed to load ×1", All(Check(report, "devices.network")), StringComparison.Ordinal);
        Assert.Equal(CheckStatus.NotFound, Check(report, "drivers").Status);
    }

    // ---- Windows settings: shown, handed off, never the verdict --------------------------------------------------------

    [Fact]
    public void AirplaneModeIsASettingThatNeverChangesTheVerdict()
    {
        var clean = new Inputs().Run();
        var inputs = new Inputs();
        inputs.Settings = inputs.Settings! with { AirplaneMode = true, WifiRadios = [new(WifiName, RadioSwitch.Off, RadioSwitch.On)] };
        var report = inputs.Run();
        var settings = Check(report, "settings.network");
        Assert.Equal(CheckDomain.DeviceSettings, settings.Domain);
        Assert.Equal(CheckStatus.Found, settings.Status);
        Assert.False(settings.Minor);
        Assert.StartsWith("Airplane mode is on", settings.Summary, StringComparison.Ordinal);
        Assert.Equal(clean.Verdict.Outcome, report.Verdict.Outcome);
        Assert.Equal(clean.Verdict.Headline, report.Verdict.Headline);
        Assert.Equal(clean.Verdict.Detail, report.Verdict.Detail);
        Assert.Equal(TriageLabels.ExitCode(clean.Verdict.Outcome), TriageLabels.ExitCode(report.Verdict.Outcome));
    }

    [Fact]
    public void HandoffSaysAWindowsSettingIsBlockingWhenHardwareLooksClean()
    {
        var inputs = new Inputs();
        inputs.Settings = inputs.Settings! with { WifiRadios = [new(WifiName, RadioSwitch.On, RadioSwitch.Off)] };
        var handoff = Handoff.Compose(inputs.Run(), [], null);
        Assert.Equal("Hardware and drivers look clean; a Windows setting is blocking Wi-Fi or networking", handoff.Headline);
        Assert.Contains("hardware switch or function key", handoff.Detail, StringComparison.Ordinal);
        Assert.Contains("not a fault", handoff.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void HardwareEvidenceStillLeadsTheHandoffWhenASettingIsAlsoFound()
    {
        var inputs = new Inputs();
        inputs.Devices[0] = Adapter(code: 43);
        inputs.Settings = inputs.Settings! with { AirplaneMode = true, WifiRadios = [new(WifiName, RadioSwitch.Off, RadioSwitch.On)] };
        var handoff = Handoff.Compose(inputs.Run(), [], null);
        Assert.Contains("evidence exists", handoff.Headline, StringComparison.Ordinal);
        Assert.Contains("A Windows setting is also blocking", handoff.Detail, StringComparison.Ordinal);
    }

    private static ReliabilityEvent AppCrash(string app, string module)
        => new(Now.AddHours(-2), "Application", Random.Shared.NextInt64(1, long.MaxValue), "Application Error", 1000, "Applications", "Error", app, "Crash", "",
            $"<Event><EventData><Data Name='AppName'>{app}</Data><Data Name='ModuleName'>{module}</Data><Data Name='ExceptionCode'>c0000005</Data><Data Name='ModulePath'>C:\\Program Files\\Teams\\{module}</Data></EventData></Event>");

    [Fact]
    public void BlockingSettingDoesNotHideCrashesOrLiveLeadsInTheHandoff()
    {
        var inputs = new Inputs();
        inputs.Settings = inputs.Settings! with { WifiRadios = [new(WifiName, RadioSwitch.Off, RadioSwitch.On)] };
        inputs.SystemEvents.AddRange(Enumerable.Range(0, 3).Select(_ => AppCrash("ms-teams.exe", "Teams.dll")));
        var report = inputs.Run();
        var quiet = Handoff.Compose(report, [], null);
        Assert.StartsWith("Hardware and drivers look clean; a Windows setting is blocking Wi-Fi or networking", quiet.Headline, StringComparison.Ordinal);
        Assert.Contains("Application crashes are on record", quiet.Detail, StringComparison.Ordinal);

        var busy = Handoff.Compose(report, [new DiagnosticFinding("Memory pressure", PressureState.Elevated, "x", [])], "chrome.exe");
        Assert.Contains("flagged for Memory pressure", busy.Detail, StringComparison.Ordinal);
        Assert.Contains("chrome.exe", busy.Detail, StringComparison.Ordinal);

        var platform = Handoff.Compose(report, [new DiagnosticFinding("High read latency", PressureState.Elevated, "x", [])], null);
        Assert.StartsWith("The current constraint points at the platform", platform.Headline, StringComparison.Ordinal);
        Assert.Contains("Wi-Fi is turned off in Windows", platform.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void StoppedWlanServiceAndOnlyAdapterDisabledAreBlocking()
    {
        var inputs = new Inputs { Settings = new DeviceSettings(Now, []) { AirplaneMode = false, Services = [new("WlanSvc", true, "Stopped", "Disabled")] } };
        inputs.Devices[0] = Adapter(code: 22);
        var report = inputs.Run();
        var settings = Check(report, "settings.network");
        Assert.False(settings.Minor);
        Assert.Contains($"{WifiName} is disabled in Device Manager.", All(settings), StringComparison.Ordinal);
        Assert.Contains("WLAN AutoConfig service is stopped (start type Disabled)", All(settings), StringComparison.Ordinal);
        Assert.Equal(CheckStatus.NotFound, Check(report, "devices.network").Status);
        Assert.Equal(TriageOutcome.NoHardwareEvidence, report.Verdict.Outcome);
    }

    [Fact]
    public void DisabledSecondAdapterIsOnlyNotedWhenAnotherOfTheSameKindIsEnabled()
    {
        var inputs = new Inputs();
        inputs.Devices.Add(Adapter(id: @"PCI\VEN_8086&DEV_7A70&SUBSYS_00748086&REV_11\3&1&0&A3", name: "Intel(R) Wi-Fi 6 AX201 160MHz", code: 22, service: "Netwtw14"));
        inputs.Devices.Add(Adapter(id: Ethernet, name: EthernetName, service: "rt25cx21"));
        var settings = Check(inputs.Run(), "settings.network");
        Assert.Equal(CheckStatus.Found, settings.Status);
        Assert.True(settings.Minor);
        Assert.Equal("Noted", TriageLabels.Status(settings));
        Assert.Contains("No hardware evidence", Handoff.Compose(inputs.Run(), [], null).Headline, StringComparison.Ordinal);
    }

    // ---- Bluetooth -----------------------------------------------------------------------------------------------------

    private const string BtRadio = @"USB\VID_8087&PID_0026\5&CC3F949&0&14";
    private const string BtName = "Intel(R) Wireless Bluetooth(R)";
    private const string Paired = @"BTHENUM\{0000110B-0000-1000-8000-00805F9B34FB}_VID&0001004C_PID&2014\7&4AF2E5&0&542A4316F033_C00000000";

    private static DeviceRecord Radio(uint? code = 0, bool? present = true)
        => new(BtRadio, BtName, DeviceClass.Bluetooth, "Bluetooth", present, code, "BTHUSB", true);

    private static Inputs WithBluetooth(Action<Inputs>? configure = null)
    {
        var inputs = new Inputs
        {
            Drivers = new DriverInventory(Now, [Driver(), Driver(BtRadio, "23.90.0.8", name: BtName) with { Class = "Bluetooth" }], []),
            Baseline = new DriverSnapshot(Now.AddDays(-7), "PC", null, "1.0", "25H2", [Driver(), Driver(BtRadio, "23.90.0.8", name: BtName) with { Class = "Bluetooth" }])
        };
        inputs.Devices.Add(Radio());
        inputs.Settings = inputs.Settings! with { BluetoothRadios = [new("Bluetooth", RadioSwitch.On, RadioSwitch.On)], BluetoothDisallowedByPolicy = false,
            Services = [new("WlanSvc", true, "Running", "Auto"), new("bthserv", true, "Running", "Manual")] };
        configure?.Invoke(inputs);
        return inputs;
    }

    [Fact]
    public void HealthyBluetoothRadioSaysNothingPointsToItsDriver()
    {
        var report = WithBluetooth().Run();
        Assert.Equal(CheckStatus.NotFound, Check(report, "devices.bluetooth").Status);
        Assert.Equal(CheckStatus.NotFound, Check(report, "settings.bluetooth").Status);
        Assert.Contains("does not support a driver update", Note(report, "Bluetooth radio driver").Text, StringComparison.Ordinal);
    }

    [Fact]
    public void BluetoothRadioThatStoppedEnumeratingIsSignificant()
    {
        var inputs = WithBluetooth(inputs => inputs.Devices[^1] = Radio(code: null, present: false));
        var check = Check(inputs.Run(), "devices.bluetooth");
        Assert.Equal(CheckStatus.Found, check.Status);
        Assert.False(check.Minor);
        Assert.Contains("stopped enumerating", All(check), StringComparison.Ordinal);
    }

    [Fact]
    public void PairedDeviceProblemIsMinorAndNotCountedTwice()
    {
        var inputs = WithBluetooth(inputs =>
        {
            inputs.Devices.Add(new(Paired, "Alice's AirPods Pro Hands-Free", DeviceClass.Bluetooth, "Bluetooth", true, 10, null, false));
            inputs.System.DeviceProblems.Add(new("Alice's AirPods Pro Hands-Free", 10, Paired));
        });
        var report = inputs.Run();
        var check = Check(report, "devices.bluetooth");
        Assert.True(check.Minor);
        Assert.Contains("Paired device · Alice's AirPods Pro Hands-Free · Code 10", All(check), StringComparison.Ordinal);
        Assert.Equal(CheckStatus.NotFound, Check(report, "devices").Status);
        Assert.Equal(TriageOutcome.MinorFindings, report.Verdict.Outcome);
    }

    [Fact]
    public void RoutineBluetoothWarningsAreIgnoredButPortDriverErrorsCount()
    {
        var inputs = WithBluetooth(inputs =>
        {
            inputs.DeviceEvents.Add(Event("BTHUSB", 17, details: "The local Bluetooth adapter does not support an important Low Energy controller state."));
            inputs.DeviceEvents.Add(Event("BTHPORT", 5, details: "A remote device tried to connect."));
        });
        var quiet = Check(inputs.Run(), "devices.bluetooth");
        Assert.Equal(CheckStatus.NotFound, quiet.Status);
        Assert.Contains("BTHUSB 17", quiet.Limit, StringComparison.Ordinal);

        for (var i = 0; i < 3; i++) inputs.DeviceEvents.Add(Event("BTHPORT", 1, "Error", "The local Bluetooth adapter has failed and will not be used."));
        var failed = Check(inputs.Run(), "devices.bluetooth");
        Assert.False(failed.Minor);
        Assert.Contains($"BTHPORT ({BtName} driver) error event 1 ×3", All(failed), StringComparison.Ordinal);
        Assert.DoesNotContain("remote device", All(failed), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(RadioSwitch.Off, RadioSwitch.On, "Bluetooth is turned off in Windows")]
    [InlineData(RadioSwitch.Unknown, RadioSwitch.Off, "held off by firmware or a hardware switch")]
    public void BluetoothSwitchedOffIsABlockingSettingNotEvidence(RadioSwitch software, RadioSwitch hardware, string expected)
    {
        var report = WithBluetooth(inputs => inputs.Settings = inputs.Settings! with { BluetoothRadios = [new("Bluetooth", software, hardware)] }).Run();
        var settings = Check(report, "settings.bluetooth");
        Assert.False(settings.Minor);
        Assert.Contains(expected, settings.Summary, StringComparison.Ordinal);
        Assert.Equal(TriageOutcome.NoHardwareEvidence, report.Verdict.Outcome);
        Assert.Equal("Hardware and drivers look clean; a Windows setting is blocking Bluetooth", Handoff.Compose(report, [], null).Headline);
    }

    [Fact]
    public void BluetoothPolicyAndDisabledServiceAreBlocking()
    {
        var report = WithBluetooth(inputs => inputs.Settings = inputs.Settings! with
        {
            BluetoothDisallowedByPolicy = true, Services = [new("WlanSvc", true, "Running", "Auto"), new("bthserv", true, "Stopped", "Disabled")]
        }).Run();
        var settings = Check(report, "settings.bluetooth");
        Assert.Contains("AllowBluetooth = 0", All(settings), StringComparison.Ordinal);
        Assert.Contains("bthserv) is disabled", All(settings), StringComparison.Ordinal);
    }

    [Fact]
    public void StoppedOnDemandBluetoothServiceIsNormal()
        => Assert.Equal(CheckStatus.NotFound, Check(WithBluetooth(inputs => inputs.Settings = inputs.Settings! with
            { Services = [new("WlanSvc", true, "Running", "Auto"), new("bthserv", true, "Stopped", "Manual")] }).Run(), "settings.bluetooth").Status);

    [Fact]
    public void AirplaneModeIsDroppedWhenARadioWasTurnedBackOn()
    {
        // Both radios read on inside airplane mode: the user turned them back on, so airplane mode blocks neither.
        var report = WithBluetooth(inputs => inputs.Settings = inputs.Settings! with { AirplaneMode = true }).Run();
        Assert.Equal(CheckStatus.NotFound, Check(report, "settings.bluetooth").Status);
        Assert.Equal(CheckStatus.NotFound, Check(report, "settings.network").Status);

        var wifiOff = WithBluetooth(inputs => inputs.Settings = inputs.Settings! with { AirplaneMode = true, WifiRadios = [new(WifiName, RadioSwitch.Off, RadioSwitch.On)] }).Run();
        Assert.Equal(CheckStatus.NotFound, Check(wifiOff, "settings.bluetooth").Status);
        Assert.StartsWith("Airplane mode is on", Check(wifiOff, "settings.network").Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyWirelessInterfaceListIsUnreadNotOn()
    {
        var report = new Inputs { Settings = new DeviceSettings(Now, []) { AirplaneMode = false, Services = [new("WlanSvc", true, "Running", "Auto")], WifiRadios = [] } }.Run();
        var settings = Check(report, "settings.network");
        Assert.Equal(CheckStatus.CouldNotCheck, settings.Status);
        Assert.Contains("Wi-Fi radio state", settings.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void UnreadableStatusOfAPresentDeviceIsCouldNotCheck()
    {
        var inputs = new Inputs();
        inputs.Devices.Add(Adapter(id: Ethernet, name: EthernetName, service: "rt25cx21", code: null));
        var report = inputs.Run();
        var check = Check(report, "devices.network");
        Assert.Equal(CheckStatus.CouldNotCheck, check.Status);
        Assert.Contains("status of 1 of 2 present network adapter(s) could not be read", check.Summary, StringComparison.Ordinal);
        Assert.Equal(UpdateLean.CouldNotAssess, Note(report, "Network adapter driver").Lean);
    }

    [Fact]
    public void PairedDeviceLoadFailureStaysWithTheDriversCheck()
    {
        var inputs = WithBluetooth(inputs =>
        {
            inputs.Devices.Add(new(Paired, "Alice's AirPods Pro Hands-Free", DeviceClass.Bluetooth, "Bluetooth", true, 0, null, false));
            inputs.SystemEvents.Add(new(Now.AddDays(-1), "System", 11, "Microsoft-Windows-Kernel-PnP", 219, "Drivers & storage", "Warning", "x", @"The driver \Driver\BthHFEnum failed to load", "",
                $"<Event><EventData><Data Name='DriverName'>{Paired.Replace("&", "&amp;", StringComparison.Ordinal)}</Data><Data Name='FailureName'>\\Driver\\BthHFEnum</Data></EventData></Event>"));
        });
        var report = inputs.Run();
        Assert.Equal(CheckStatus.Found, Check(report, "drivers").Status);
        Assert.Contains("Microsoft-Windows-Kernel-PnP event 219", All(Check(report, "drivers")), StringComparison.Ordinal);
        Assert.DoesNotContain("failed to load", All(Check(report, "devices.bluetooth")), StringComparison.Ordinal);
    }

    [Fact]
    public void UnreadableBluetoothRadioStateIsCouldNotCheck()
        => Assert.Equal(CheckStatus.CouldNotCheck, Check(WithBluetooth(inputs => inputs.Settings = inputs.Settings! with { BluetoothRadios = null }).Run(), "settings.bluetooth").Status);

    [Fact]
    public void NoBluetoothRadioMeansBluetoothChecksAreNotApplicable()
    {
        var report = new Inputs().Run();
        Assert.Equal(CheckStatus.NotApplicable, Check(report, "devices.bluetooth").Status);
        Assert.Equal(CheckStatus.NotApplicable, Check(report, "settings.bluetooth").Status);
    }

    // ---- Audio ---------------------------------------------------------------------------------------------------------

    private const string Codec = @"HDAUDIO\FUNC_01&VEN_10EC&DEV_0897&SUBSYS_104387D7&REV_1001\5&2C2F8F2B&0&0001";
    private const string Controller = @"PCI\VEN_8086&DEV_7AD0&SUBSYS_87D71043&REV_11\3&11583659&0&FB";

    private static Inputs WithAudio(Action<Inputs>? configure = null)
    {
        var drivers = new[] { Driver(), Driver(Codec, "6.0.9888.1", name: "Realtek(R) Audio") with { Class = "MEDIA" }, Driver(Controller, "10.0.26100.1", name: "High Definition Audio Controller") with { Class = "System", Provider = "Microsoft" } };
        var inputs = new Inputs { Drivers = new DriverInventory(Now, drivers, []), Baseline = new DriverSnapshot(Now.AddDays(-7), "PC", null, "1.0", "25H2", drivers) };
        inputs.Devices.Add(new(Codec, "Realtek(R) Audio", DeviceClass.Audio, "MEDIA", true, 0, "IntcAzAudAddService", true));
        inputs.Devices.Add(new(Controller, "High Definition Audio Controller", DeviceClass.Audio, "System", true, 0, "HDAudBus", true));
        inputs.Settings = inputs.Settings! with
        {
            Services = [new("WlanSvc", true, "Running", "Auto"), new("Audiosrv", true, "Running", "Auto"), new("AudioEndpointBuilder", true, "Running", "Auto")],
            Audio = new(new("Speakers (Realtek(R) Audio)", false, 0.5), new("Microphone (Jabra)", false, 0.8), 2, 0, 1, 1, 0),
            Microphone = new PrivacyReading("microphone") { DeviceDenied = false, UserDenied = false, DesktopAppsDenied = false, DeniedApps = [] }
        };
        configure?.Invoke(inputs);
        return inputs;
    }

    private static ReliabilityEvent AudioEngineCrash(string module)
        => new(Now.AddHours(-3), "Application", Random.Shared.NextInt64(1, long.MaxValue), "Application Error", 1000, "Applications", "Error", "audiodg.exe", "Crash", "",
            $"<Event><EventData><Data Name='AppName'>audiodg.exe</Data><Data Name='ModuleName'>{module}</Data><Data Name='ExceptionCode'>c0000005</Data><Data Name='ModulePath'>C:\\Windows\\System32\\{module}</Data></EventData></Event>");

    [Fact]
    public void HealthyAudioComparesTheControllerDriverEvenThoughItIsASystemDevice()
    {
        var report = WithAudio().Run();
        var check = Check(report, "devices.audio");
        Assert.Equal(CheckStatus.NotFound, check.Status);
        Assert.Contains("High Definition Audio Controller: Microsoft 10.0.26100.1", All(check), StringComparison.Ordinal);
        Assert.DoesNotContain("not in golden.json", All(check), StringComparison.Ordinal);
        Assert.Equal(CheckStatus.NotFound, Check(report, "settings.audio").Status);
        Assert.Contains("does not support a driver update", Note(report, "Audio device driver").Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AudioEngineCrashesInADriverSuppliedModuleAreEvidenceWhenRepeated()
    {
        var once = WithAudio(inputs => inputs.SystemEvents.Add(AudioEngineCrash("RTKAPO64.dll")));
        var minor = Check(once.Run(), "devices.audio");
        Assert.True(minor.Minor);
        Assert.Contains("audiodg.exe) crashed inside RTKAPO64.dll ×1", All(minor), StringComparison.Ordinal);

        var repeated = WithAudio(inputs => inputs.SystemEvents.AddRange(Enumerable.Range(0, 3).Select(_ => AudioEngineCrash("RTKAPO64.dll"))));
        Assert.False(Check(repeated.Run(), "devices.audio").Minor);

        var windows = WithAudio(inputs => inputs.SystemEvents.AddRange(Enumerable.Range(0, 3).Select(_ => AudioEngineCrash("AUDIOENG.dll"))));
        Assert.Equal(CheckStatus.NotFound, Check(windows.Run(), "devices.audio").Status);
    }

    [Fact]
    public void MutedSpeakersAndDisabledPlaybackAreSettingsNotEvidence()
    {
        var report = WithAudio(inputs => inputs.Settings = inputs.Settings! with { Audio = new(new("Speakers (Realtek(R) Audio)", true, 0.5), null, 0, 2, 0, 0, 0) }).Run();
        var settings = Check(report, "settings.audio");
        Assert.False(settings.Minor);
        Assert.Contains("No playback device is active; 2 are disabled in Sound settings.", All(settings), StringComparison.Ordinal);
        Assert.Contains("Speakers (Realtek(R) Audio)) is muted", All(settings), StringComparison.Ordinal);
        Assert.Equal(TriageOutcome.NoHardwareEvidence, report.Verdict.Outcome);
        Assert.Equal("Hardware and drivers look clean; a Windows setting is blocking sound or the microphone", Handoff.Compose(report, [], null).Headline);
    }

    [Fact]
    public void ZeroVolumeAndMutedMicrophoneAreReported()
    {
        var settings = Check(WithAudio(inputs => inputs.Settings = inputs.Settings! with { Audio = new(new("Speakers", false, 0.0), new("Microphone (Jabra)", true, 0.8), 1, 0, 0, 1, 0) }).Run(), "settings.audio");
        Assert.Contains("Speakers) is at 0% volume", All(settings), StringComparison.Ordinal);
        Assert.Contains("Microphone (Jabra)) is muted", All(settings), StringComparison.Ordinal);
    }

    [Fact]
    public void StoppedWindowsAudioServiceIsBlocking()
        => Assert.Contains("Windows Audio service is stopped", Check(WithAudio(inputs => inputs.Settings = inputs.Settings! with
            { Services = [new("Audiosrv", true, "Stopped", "Disabled"), new("AudioEndpointBuilder", true, "Running", "Auto")] }).Run(), "settings.audio").Summary, StringComparison.Ordinal);

    [Fact]
    public void MicrophonePrivacyNamesTheSupportedApps()
    {
        var settings = Check(WithAudio(inputs => inputs.Settings = inputs.Settings! with
        {
            Microphone = new PrivacyReading("microphone") { DeviceDenied = false, UserDenied = false, DesktopAppsDenied = true, DeniedApps = ["Microsoft Teams"] }
        }).Run(), "settings.audio");
        Assert.Contains("blocks Zoom, Google Meet in a browser, and classic Teams", All(settings), StringComparison.Ordinal);
        Assert.Contains("Microsoft Teams is not allowed to use the microphone", All(settings), StringComparison.Ordinal);
    }

    [Fact]
    public void AnotherAccountsSessionMakesSoundSettingsCouldNotCheck()
    {
        const string why = "Skald is running under a different account than the user signed in to this session.";
        var report = WithAudio(inputs => inputs.Settings = inputs.Settings! with
        {
            SessionProblem = why, Audio = null, Microphone = new PrivacyReading("microphone") { DeviceDenied = false }
        }).Run();
        var settings = Check(report, "settings.audio");
        Assert.Equal(CheckStatus.CouldNotCheck, settings.Status);
        Assert.Contains("different account", settings.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void MachineWidePrivacyBlockIsStillFoundWhenPerUserSwitchesCannotBeRead()
    {
        var report = WithAudio(inputs => inputs.Settings = inputs.Settings! with
        {
            SessionProblem = "Skald is running as SYSTEM.", Microphone = new PrivacyReading("microphone") { DeviceDenied = true }
        }).Run();
        var settings = Check(report, "settings.audio");
        Assert.Equal(CheckStatus.Found, settings.Status);
        Assert.Contains("Could not read", settings.Limit, StringComparison.Ordinal);
    }

    // ---- Camera --------------------------------------------------------------------------------------------------------

    private const string Webcam = @"USB\VID_046D&PID_085E&MI_00\7&2A&0&0000";

    private static Inputs WithCamera(Action<Inputs>? configure = null)
    {
        var inputs = new Inputs();
        inputs.Devices.Add(new(Webcam, "Integrated Camera", DeviceClass.Camera, "Camera", true, 0, "usbvideo", true));
        inputs.Settings = inputs.Settings! with
        {
            Services = [new("WlanSvc", true, "Running", "Auto"), new("FrameServer", true, "Stopped", "Manual")],
            Camera = new PrivacyReading("webcam") { DeviceDenied = false, UserDenied = false, DesktopAppsDenied = false, DeniedApps = [] }
        };
        configure?.Invoke(inputs);
        return inputs;
    }

    [Fact]
    public void HealthyCameraWithAccessAllowedIsClean()
    {
        var report = WithCamera().Run();
        Assert.Equal(CheckStatus.NotFound, Check(report, "devices.camera").Status);
        Assert.Equal(CheckStatus.NotFound, Check(report, "settings.camera").Status);
    }

    [Fact]
    public void VirtualCameraIsNotHardware()
    {
        var inputs = new Inputs();
        inputs.Devices.Add(new(@"SWD\VCAMDEVAPI\B6C9A4E2", "OBS Virtual Camera", DeviceClass.Camera, "Camera", true, 10, null, false));
        Assert.Equal(CheckStatus.NotApplicable, Check(inputs.Run(), "devices.camera").Status);
    }

    [Fact]
    public void CameraPolicyAndPrivacySwitchesAreBlockingSettings()
    {
        var report = WithCamera(inputs => inputs.Settings = inputs.Settings! with
        {
            Camera = new PrivacyReading("webcam") { DeviceDenied = false, UserDenied = false, DesktopAppsDenied = true, DeniedApps = ["Windows Camera app"], DisabledByPolicy = true }
        }).Run();
        var settings = Check(report, "settings.camera");
        Assert.False(settings.Minor);
        Assert.Contains("Allow use of camera = 0", All(settings), StringComparison.Ordinal);
        Assert.Contains("blocks Zoom, Google Meet in a browser, and classic Teams", All(settings), StringComparison.Ordinal);
        Assert.Contains("Windows Camera app is not allowed to use the camera", All(settings), StringComparison.Ordinal);
        Assert.Equal(TriageOutcome.NoHardwareEvidence, report.Verdict.Outcome);
        Assert.Equal("Hardware and drivers look clean; a Windows setting is blocking the camera", Handoff.Compose(report, [], null).Headline);
    }

    [Fact]
    public void CameraInUseByAnotherAppIsOnlyNoted()
    {
        var settings = Check(WithCamera(inputs => inputs.Settings = inputs.Settings! with
        {
            Camera = new PrivacyReading("webcam") { DeviceDenied = false, UserDenied = false, DesktopAppsDenied = false, DeniedApps = [], InUseBy = ["Zoom.exe"] }
        }).Run(), "settings.camera");
        Assert.True(settings.Minor);
        Assert.Contains("in use right now by Zoom.exe", settings.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledFrameServerIsBlocking()
        => Assert.Contains("Frame Server service is disabled", Check(WithCamera(inputs => inputs.Settings = inputs.Settings! with
            { Services = [new("FrameServer", true, "Stopped", "Disabled")] }).Run(), "settings.camera").Summary, StringComparison.Ordinal);

    [Fact]
    public void UnreadablePerUserCameraSwitchesAreCouldNotCheck()
    {
        var settings = Check(WithCamera(inputs => inputs.Settings = inputs.Settings! with
        {
            SessionProblem = "Skald is running as SYSTEM.", Camera = new PrivacyReading("webcam") { DeviceDenied = false }
        }).Run(), "settings.camera");
        Assert.Equal(CheckStatus.CouldNotCheck, settings.Status);
        Assert.Contains("running as SYSTEM", settings.Summary, StringComparison.Ordinal);
    }

    // ---- Display -------------------------------------------------------------------------------------------------------

    private const string Panel = @"DISPLAY\BOE0900\4&1A&0&UID8388688";
    private const string External = @"DISPLAY\MSI3CD7\5&2B&0&UID4352";

    private static Inputs WithDisplays(DisplayReading? layout, bool external = true, Action<Inputs>? configure = null)
    {
        var inputs = new Inputs
        {
            Drivers = new DriverInventory(Now, [Driver(), Driver(@"PCI\VEN_10DE&DEV_2684&SUBSYS_00000000&REV_A1\4&1&0&0008", "32.0.16.1088", name: "NVIDIA GeForce RTX 4090") with { Class = "Display", Provider = "NVIDIA" }], [])
        };
        inputs.Devices.Add(new(Panel, "Generic PnP Monitor", DeviceClass.Display, "Monitor", true, 0, "monitor", true));
        if (external) inputs.Devices.Add(new(External, "Generic Monitor (MPG271QX OLED)", DeviceClass.Display, "Monitor", true, 0, "monitor", false));
        inputs.Settings = inputs.Settings! with { Display = layout };
        configure?.Invoke(inputs);
        return inputs;
    }

    [Fact]
    public void PcScreenOnlyWithAnExternalMonitorIsBlocking()
    {
        var report = WithDisplays(new(DisplayTopology.PcScreenOnly, 1)).Run();
        var settings = Check(report, "settings.display");
        Assert.False(settings.Minor);
        Assert.Contains("\"PC screen only\" (Win+P), so 1 other connected monitor(s) stay dark", settings.Summary, StringComparison.Ordinal);
        Assert.Equal("Hardware and drivers look clean; a Windows setting is blocking a display", Handoff.Compose(report, [], null).Headline);
    }

    [Fact]
    public void ExtendedDesktopIsCleanAndPcScreenOnlyAloneIsFine()
    {
        Assert.Equal(CheckStatus.NotFound, Check(WithDisplays(new(DisplayTopology.Extend, 2)).Run(), "settings.display").Status);
        Assert.Equal(CheckStatus.NotFound, Check(WithDisplays(new(DisplayTopology.PcScreenOnly, 1), external: false).Run(), "settings.display").Status);
    }

    [Fact]
    public void ConnectedButUnusedMonitorIsOnlyNoted()
        => Assert.True(Check(WithDisplays(new(DisplayTopology.Extend, 1)).Run(), "settings.display").Minor);

    [Fact]
    public void RemoteSessionMakesDisplayLayoutCouldNotCheck()
    {
        var settings = Check(WithDisplays(null, configure: inputs => inputs.Settings = inputs.Settings! with { RemoteSession = "Skald is running in a Remote Desktop session." }).Run(), "settings.display");
        Assert.Equal(CheckStatus.CouldNotCheck, settings.Status);
        Assert.Contains("Remote Desktop", settings.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingBuiltInScreenIsEvidenceButAnUnpluggedMonitorIsNot()
    {
        var unplugged = WithDisplays(new(DisplayTopology.Extend, 1), configure: inputs =>
        {
            inputs.Baseline = new DriverSnapshot(Now.AddDays(-7), "PC", null, "1.0", "25H2", [Driver(), Driver(Panel, "1.0", name: "Generic PnP Monitor") with { Class = "MONITOR" }, Driver(External, "1.0", name: "MSI") with { Class = "MONITOR" }]);
            inputs.Devices[^1] = inputs.Devices[^1] with { Present = false, ProblemCode = null };
        });
        Assert.Equal(CheckStatus.NotFound, Check(unplugged.Run(), "devices.display").Status);

        var panelGone = WithDisplays(new(DisplayTopology.Extend, 1), configure: inputs =>
        {
            inputs.Baseline = new DriverSnapshot(Now.AddDays(-7), "PC", null, "1.0", "25H2", [Driver(), Driver(Panel, "1.0", name: "Generic PnP Monitor") with { Class = "MONITOR" }]);
            inputs.Devices[^2] = inputs.Devices[^2] with { Present = false, ProblemCode = null };
        });
        Assert.False(Check(panelGone.Run(), "devices.display").Minor);
    }

    [Fact]
    public void GraphicsAdapterDriverIsListedBesideMonitorsButCountedOnlyUnderGraphics()
    {
        var report = WithDisplays(new(DisplayTopology.Extend, 2)).Run();
        var display = Check(report, "devices.display");
        Assert.Equal(CheckStatus.NotFound, display.Status);
        Assert.Contains("Graphics adapter driver (evidence under Graphics driver and GPU) · NVIDIA GeForce RTX 4090", All(display), StringComparison.Ordinal);
        Assert.Contains("No baseline", Note(new Inputs { Baseline = null }.Run(), "Graphics driver").Text, StringComparison.Ordinal);
    }

    // ---- Keyboard, mouse and touchpad ----------------------------------------------------------------------------------

    private const string BuiltInKeyboard = @"ACPI\ASUS2020\4&1&0";
    private const string Touchpad = @"HID\VEN_ELAN&DEV_1200&Col01\5&2&0&0000";
    private const string UsbMouse = @"HID\VID_046D&PID_C52B&MI_01&Col01\8&3&0&0000";

    private static Inputs WithInput(InputReading? input, bool mouse = true, Action<Inputs>? configure = null)
    {
        var inputs = new Inputs();
        inputs.Devices.Add(new(BuiltInKeyboard, "Standard PS/2 Keyboard", DeviceClass.Input, "Keyboard", true, 0, "i8042prt", true));
        inputs.Devices.Add(new(Touchpad, "HID-compliant touch pad", DeviceClass.Input, "Mouse", true, 0, "mouhid", true));
        if (mouse) inputs.Devices.Add(new(UsbMouse, "HID-compliant mouse", DeviceClass.Input, "Mouse", true, 0, "mouhid", false));
        inputs.Settings = inputs.Settings! with { Input = input };
        configure?.Invoke(inputs);
        return inputs;
    }

    [Fact]
    public void FilterKeysAndMouseKeysAreBlockingSettings()
    {
        var report = WithInput(new(true, true, true, false)).Run();
        var settings = Check(report, "settings.input");
        Assert.Contains("Filter Keys is on", All(settings), StringComparison.Ordinal);
        Assert.Contains("Mouse Keys is on", All(settings), StringComparison.Ordinal);
        Assert.Equal("Hardware and drivers look clean; a Windows setting is blocking the keyboard, mouse or touchpad", Handoff.Compose(report, [], null).Headline);
    }

    [Fact]
    public void TouchpadOffWithMouseOnlyMattersWhenAMouseIsConnected()
    {
        Assert.Contains("turn off while a mouse is connected", Check(WithInput(new(false, false, true, true)).Run(), "settings.input").Summary, StringComparison.Ordinal);
        Assert.Equal(CheckStatus.NotFound, Check(WithInput(new(false, false, true, true), mouse: false).Run(), "settings.input").Status);
        Assert.Contains("touchpad is turned off", Check(WithInput(new(false, false, false, false)).Run(), "settings.input").Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void UnreadableInputSettingsAreCouldNotCheck()
        => Assert.Equal(CheckStatus.CouldNotCheck, Check(WithInput(null).Run(), "settings.input").Status);

    [Fact]
    public void BuiltInKeyboardThatStoppedEnumeratingIsEvidenceAndHarmlessReflectorFailuresAreIgnored()
    {
        var inputs = WithInput(new(false, false, true, false), configure: inputs =>
        {
            inputs.Baseline = new DriverSnapshot(Now.AddDays(-7), "PC", null, "1.0", "25H2", [Driver(), Driver(BuiltInKeyboard, "1.0", name: "Standard PS/2 Keyboard") with { Class = "KEYBOARD" }]);
            inputs.Devices[^3] = inputs.Devices[^3] with { Present = false, ProblemCode = null };
            inputs.SystemEvents.Add(new(Now.AddDays(-1), "System", 8, "Microsoft-Windows-Kernel-PnP", 219, "Drivers & storage", "Warning", "x", @"The driver \Driver\WUDFRd failed to load", "",
                $"<Event><EventData><Data Name='DriverName'>{UsbMouse.Replace("&", "&amp;", StringComparison.Ordinal)}</Data><Data Name='FailureName'>\\Driver\\WUDFRd</Data></EventData></Event>"));
        });
        var check = Check(inputs.Run(), "devices.input");
        Assert.False(check.Minor);
        Assert.Contains("Standard PS/2 Keyboard: present in golden.json", All(check), StringComparison.Ordinal);
        Assert.DoesNotContain("failed to load", All(check), StringComparison.Ordinal);
    }

    [Fact]
    public void IdenticalDriverLinesAreCollapsedWithACount()
    {
        var inputs = WithInput(new(false, false, true, false), mouse: false, configure: inputs =>
        {
            inputs.Devices.Clear();
            for (var i = 0; i < 4; i++) inputs.Devices.Add(new($@"HID\VID_046D&PID_C52B&MI_00\8&{i}&0&0000", "HID Keyboard Device", DeviceClass.Input, "Keyboard", true, 0, "kbdhid", false));
            inputs.Drivers = new DriverInventory(Now, Enumerable.Range(0, 4).Select(i => Driver($@"HID\VID_046D&PID_C52B&MI_00\8&{i}&0&0000", "10.0.1", name: "HID Keyboard Device") with { Class = "Keyboard" }).ToArray(), []);
            inputs.Baseline = null;
        });
        Assert.Contains("(×4).", All(Check(inputs.Run(), "devices.input")), StringComparison.Ordinal);
    }

    // ---- BIOS note, reports and snapshots ------------------------------------------------------------------------------

    [Fact]
    public void BiosNoteFollowsWheaAndFirmwareEvidence()
    {
        Assert.Contains("does not support a BIOS update", Note(new Inputs().Run(), "BIOS / firmware").Text, StringComparison.Ordinal);
        var fatal = new Inputs { Whea = [new(Now.AddDays(-1), "System", 9, "Microsoft-Windows-WHEA-Logger", 18, "Hardware", "Error", "x", "Fatal", "fatal", "")] };
        Assert.Equal(UpdateLean.PointsHere, Note(fatal.Run(), "BIOS / firmware").Lean);
        var unreadable = new Inputs { ReliabilityLogs = ["System: unavailable / partial (UnauthorizedAccessException)"] };
        Assert.Equal(UpdateLean.CouldNotAssess, Note(unreadable.Run(), "BIOS / firmware").Lean);
    }

    [Fact]
    public void NewSectionsAppearInEveryReportFormatAndRedactionCoversThem()
    {
        var inputs = new Inputs();
        inputs.Settings = inputs.Settings! with { AirplaneMode = true, WifiRadios = [new(WifiName, RadioSwitch.Off, RadioSwitch.On)] };
        var report = inputs.Run();
        var text = TriageReportWriter.ToText(report);
        Assert.Contains("DRIVER AND BIOS UPDATES: WHAT THE EVIDENCE SAYS", text, StringComparison.Ordinal);
        Assert.Contains("WINDOWS SETTINGS THAT CAN MAKE A WORKING DEVICE LOOK BROKEN", text, StringComparison.Ordinal);
        Assert.Contains("[SETTING FOUND] Wi-Fi and network adapter settings", text, StringComparison.Ordinal);
        Assert.Contains("Driver and BIOS updates: what the evidence says", TriageReportWriter.ToHtml(report), StringComparison.Ordinal);
        var json = TriageReportWriter.ToJson(report);
        Assert.Contains("\"updateEvidence\"", json, StringComparison.Ordinal);
        Assert.Contains("\"domain\": \"DeviceSettings\"", json, StringComparison.Ordinal);

        var named = report with { UpdateEvidence = [new("Network adapter driver", UpdateLean.NothingPointsHere, "Compared on LAB-PC-07")] };
        Assert.DoesNotContain("LAB-PC-07", TriageReportWriter.ToJson(TriageRedactor.Redact(named, "LAB-PC-07", "alice")), StringComparison.Ordinal);
    }

    [Fact]
    public void SnapshotSavedBeforeSignersWereRecordedStillLoads()
    {
        var path = Path.Combine(Path.GetTempPath(), $"skald-snapshot-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, """{"createdAt":"2026-09-01T00:00:00+00:00","computer":"PC","drivers":[{"deviceId":"PCI\\X","name":"Wi-Fi","class":"NET","version":"1.0","provider":"Intel","inf":"a.inf","isSigned":true}]}""");
        try
        {
            var snapshot = DriverSnapshotStore.Load(path, new List<string>());
            Assert.NotNull(snapshot);
            Assert.Null(snapshot.Drivers.Single().Signer);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(@"PCI\VEN_8086&DEV_2725&SUBSYS_00248086&REV_1A\4&1A&0&00E0", @"PCI\VEN_8086&DEV_2725")]
    [InlineData(@"USB\VID_8087&PID_0026\5&CC3F949&0&14", @"USB\VID_8087&PID_0026")]
    [InlineData(@"ROOT\NET\0000", @"ROOT\NET")]
    public void HardwareKeyKeepsVendorAndDeviceOnly(string instanceId, string expected) => Assert.Equal(expected, DeviceRecord.HardwareKeyOf(instanceId));
}
