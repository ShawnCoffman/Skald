using Skald.Core.Models;
using Skald.Triage;
using Xunit;

namespace Skald.Core.Tests;

public sealed class TriageEngineTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly string[] ReadableLogs = ["System: 100 records scanned", "Application: 50 records scanned"];

    private static ReliabilityEvent Event(string provider, int id, string category, string raw = "", string details = "", string summary = "Summary",
        string component = "Component", double daysAgo = 1, string severity = "Error", long record = 0)
        => new(Now.AddDays(-daysAgo), "System", record == 0 ? Random.Shared.NextInt64(1, long.MaxValue) : record, provider, id, category, severity, component, summary, details, raw);

    private static TriageReport Run(IEnumerable<ReliabilityEvent> events, Action<TriageInputsBuilder>? configure = null, string[]? logs = null)
    {
        var builder = new TriageInputsBuilder { Events = events.ToArray(), Logs = logs ?? ReadableLogs };
        configure?.Invoke(builder);
        return TriageEngine.Evaluate(builder.Build(), "test");
    }

    private sealed class TriageInputsBuilder
    {
        public ReliabilityEvent[] Events { get; set; } = [];
        public string[] Logs { get; set; } = [];
        public DriverSnapshot? Baseline { get; set; }
        public DriverInventory? Drivers { get; set; }
        public SystemInventory System { get; } = new() { DevicesAvailable = true, StorageAvailable = true };
        public HardwareInventory? Hardware { get; set; }
        public WindowsUpdateHistory? Updates { get; set; }
        public Dictionary<string, DateTimeOffset> Truncated { get; } = [];
        public CrashDumpInventory? Dumps { get; set; }
        public int Days { get; set; } = 30;
        public TriageInputs Build() => new(Now, Days, false)
        {
            Reliability = new(Now, Events, Logs) { TruncatedAt = Truncated }, System = System, Baseline = Baseline, Drivers = Drivers,
            Hardware = Hardware, Updates = Updates, Dumps = Dumps
        };
    }

    private static TriageCheck Check(TriageReport report, string id) => report.Checks.Single(check => check.Id == id);

    private static string KernelPower41(uint bugcheck, bool longPress = false)
        => $"<Event><EventData><Data Name='BugcheckCode'>{bugcheck}</Data><Data Name='LongPowerButtonPressDetected'>{(longPress ? "true" : "false")}</Data><Data Name='WHEABootErrorCount'>0</Data></EventData></Event>";

    [Fact]
    public void CleanMachineYieldsNoHardwareEvidenceAndSaysItIsNotATest()
    {
        var report = Run([]);
        Assert.Equal(TriageOutcome.NoHardwareEvidence, report.Verdict.Outcome);
        Assert.Contains("not a hardware test", report.Verdict.Detail, StringComparison.Ordinal);
        Assert.Equal(CheckStatus.NotFound, Check(report, "crashes").Status);
        Assert.Equal(0, TriageLabels.ExitCode(report.Verdict.Outcome));
    }

    [Fact]
    public void HardwareLeaningBugcheckIsSignificantEvidence()
    {
        var report = Run([Event("Microsoft-Windows-Kernel-Power", 41, "Crashes & restarts", KernelPower41(0x124), severity: "Critical")]);
        var crashes = Check(report, "crashes");
        Assert.Equal(CheckStatus.Found, crashes.Status);
        Assert.False(crashes.Minor);
        Assert.Contains("WHEA_UNCORRECTABLE_ERROR", string.Join('\n', crashes.Details), StringComparison.Ordinal);
        Assert.Equal(TriageOutcome.HardwareEvidenceFound, report.Verdict.Outcome);
        Assert.Equal(1, TriageLabels.ExitCode(report.Verdict.Outcome));
    }

    [Fact]
    public void SingleUnexplainedShutdownIsOnlyMinor()
    {
        var report = Run([Event("Microsoft-Windows-Kernel-Power", 41, "Crashes & restarts", KernelPower41(0), severity: "Critical")]);
        Assert.True(Check(report, "crashes").Minor);
        Assert.Equal(TriageOutcome.MinorFindings, report.Verdict.Outcome);
    }

    [Fact]
    public void LongPowerButtonPressMarksAHangForcedOff()
    {
        var report = Run([Event("Microsoft-Windows-Kernel-Power", 41, "Crashes & restarts", KernelPower41(0, longPress: true), severity: "Critical")]);
        Assert.False(Check(report, "crashes").Minor);
        Assert.Contains("hung machine forced off", string.Join('\n', Check(report, "crashes").Details), StringComparison.Ordinal);
    }

    [Fact]
    public void BugcheckCodeIsReadFromEventLogMessageToo()
    {
        var report = Run([Event("Microsoft-Windows-WER-SystemErrorReporting", 1001, "Crashes & restarts", details: "The computer has rebooted from a bugcheck.  The bugcheck was: 0x00000133 (0x1, 0x2, 0x3, 0x4).")]);
        Assert.Contains("DPC_WATCHDOG_VIOLATION", string.Join('\n', Check(report, "crashes").Details), StringComparison.Ordinal);
    }

    [Fact]
    public void UnreadableSystemLogMakesTheCheckIncompleteNotClean()
    {
        var report = Run([], logs: ["System: unavailable / partial (UnauthorizedAccessException)", "Application: 5 records scanned"]);
        Assert.Equal(CheckStatus.CouldNotCheck, Check(report, "crashes").Status);
        Assert.Equal(TriageOutcome.Incomplete, report.Verdict.Outcome);
        Assert.Equal(2, TriageLabels.ExitCode(report.Verdict.Outcome));
    }

    [Fact]
    public void EventsOutsideTheWindowAreIgnored()
    {
        var report = Run([Event("Microsoft-Windows-Kernel-Power", 41, "Crashes & restarts", KernelPower41(0x124), daysAgo: 45)]);
        Assert.Equal(CheckStatus.NotFound, Check(report, "crashes").Status);
    }

    [Fact]
    public void FatalWheaIsSignificantButAFewCorrectedReportsAreMinor()
    {
        var fatal = Run([Event("Microsoft-Windows-WHEA-Logger", 18, "Hardware", details: "A fatal hardware error has occurred.")]);
        Assert.False(Check(fatal, "whea").Minor);
        var corrected = Run([Event("Microsoft-Windows-WHEA-Logger", 17, "Hardware", details: "A corrected hardware error has occurred.", severity: "Warning")]);
        Assert.Equal(CheckStatus.Found, Check(corrected, "whea").Status);
        Assert.True(Check(corrected, "whea").Minor);
    }

    [Fact]
    public void FailedMemoryDiagnosticIsEvidenceAndPassedRunIsOnlyANote()
    {
        var failed = Run([Event("Microsoft-Windows-MemoryDiagnostics-Results", 1202, "Firmware & diagnostics", severity: "Error")]);
        Assert.Equal(CheckStatus.Found, Check(failed, "memory").Status);
        var passed = Run([Event("Microsoft-Windows-MemoryDiagnostics-Results", 1201, "Firmware & diagnostics", severity: "Information")]);
        Assert.Equal(CheckStatus.NotFound, Check(passed, "memory").Status);
        Assert.Contains("short test", Check(passed, "memory").Limit, StringComparison.Ordinal);
        Assert.Contains("not been actively tested", Check(Run([]), "memory").Limit, StringComparison.Ordinal);
    }

    [Fact]
    public void FirmwareThrottleEventIsReported()
    {
        var report = Run([Event("Microsoft-Windows-Kernel-Processor-Power", 37, "Firmware & diagnostics", summary: "The speed of processor 0 is being limited by system firmware.", severity: "Warning")]);
        Assert.Equal(CheckStatus.Found, Check(report, "firmware").Status);
        Assert.True(Check(report, "firmware").Minor); // one report is routine on laptops
        var frequent = Run(Enumerable.Range(0, 12).Select(_ => Event("Microsoft-Windows-Kernel-Processor-Power", 37, "Firmware & diagnostics", severity: "Warning")));
        Assert.False(Check(frequent, "firmware").Minor);
    }

    [Fact]
    public void GenericUserModeReflectorWarningsDoNotCountAsDriverFailures()
    {
        var events = Enumerable.Range(0, 30).Select(_ => Event("Microsoft-Windows-Kernel-PnP", 219, "Drivers & storage", summary: @"The driver \Driver\WUDFRd failed to load.", severity: "Warning")).ToArray();
        var report = Run(events);
        Assert.Equal(CheckStatus.NotFound, Check(report, "drivers").Status);
        Assert.Contains("ignored", Check(report, "drivers").Limit, StringComparison.Ordinal);
        var real = Run([Event("Microsoft-Windows-Kernel-PnP", 219, "Drivers & storage", summary: @"The driver \Driver\vendorfilter failed to load.", severity: "Warning")]);
        Assert.Equal(CheckStatus.Found, Check(real, "drivers").Status);
        Assert.True(Check(real, "drivers").Minor);
    }

    [Fact]
    public void StorageControllerErrorsAreSignificant()
    {
        var report = Run([Event("disk", 11, "Drivers & storage", details: @"The driver detected a controller error on \Device\Harddisk1\DR1. for Disk 1")]);
        Assert.Equal(CheckStatus.Found, Check(report, "storage").Status);
        Assert.False(Check(report, "storage").Minor);
        var single = Run([Event("disk", 153, "Drivers & storage", details: "The IO operation at logical block address 0x1 was retried. for Disk 1")]);
        Assert.True(Check(single, "storage").Minor);
    }

    [Fact]
    public void ApplicationFaultsAreSoftwareEvidenceAndDoNotChangeTheHardwareVerdict()
    {
        const string raw = "<Event><EventData><Data Name='AppName'>teams.exe</Data><Data Name='AppVersion'>1.0</Data><Data Name='ModuleName'>teams.exe</Data><Data Name='ExceptionCode'>c0000005</Data><Data Name='ModulePath'>C:\\Program Files\\Teams\\teams.exe</Data></EventData></Event>";
        var events = Enumerable.Range(0, 3).Select(_ => new ReliabilityEvent(Now.AddHours(-2), "Application", Random.Shared.NextInt64(1, long.MaxValue), "Application Error", 1000, "Applications", "Error", "teams.exe", "Crash", "", raw)).ToArray();
        var report = Run(events);
        var faults = Check(report, "app.faults");
        Assert.Equal(CheckDomain.Software, faults.Domain);
        Assert.Equal(CheckStatus.Found, faults.Status);
        Assert.Contains("Repeats the same fault signature", string.Join('\n', faults.Details), StringComparison.Ordinal);
        Assert.Contains("application's own module", string.Join('\n', faults.Details), StringComparison.Ordinal);
        Assert.Equal(TriageOutcome.NoHardwareEvidence, report.Verdict.Outcome);
        Assert.Contains("Application-side evidence", report.Verdict.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void CrashInsideGraphicsDriverModuleCountsTowardGraphics()
    {
        const string raw = "<Event><EventData><Data Name='AppName'>game.exe</Data><Data Name='ModuleName'>nvwgf2umx.dll</Data><Data Name='ExceptionCode'>c0000005</Data><Data Name='ModulePath'>C:\\Windows\\System32\\DriverStore\\FileRepository\\nv\\nvwgf2umx.dll</Data></EventData></Event>";
        var events = Enumerable.Range(0, 2).Select(_ => new ReliabilityEvent(Now.AddHours(-2), "Application", Random.Shared.NextInt64(1, long.MaxValue), "Application Error", 1000, "Applications", "Error", "game.exe", "Crash", "", raw)).ToArray();
        var report = Run(events);
        Assert.Equal(CheckStatus.Found, Check(report, "graphics").Status);
        Assert.False(Check(report, "graphics").Minor);
    }

    [Fact]
    public void DriverChangesAgainstBaselineAreContextWithVendorDriversFirst()
    {
        DriverRecord Driver(string id, string version, string provider) => new(id, id, "Display", version, null, provider, "x.inf", true);
        var report = Run([], builder =>
        {
            builder.Baseline = new DriverSnapshot(Now.AddDays(-7), "PC", null, "1.0", "25H2", [Driver("A", "1.0.0.0", "NVIDIA"), Driver("B", "1.0.0.0", "Microsoft")]);
            builder.Drivers = new DriverInventory(Now, [Driver("A", "2.0.0.0", "NVIDIA"), Driver("B", "2.0.0.0", "Microsoft")], []);
        });
        var changes = Check(report, "changes");
        Assert.Equal(CheckDomain.Context, changes.Domain);
        Assert.Equal(CheckStatus.Found, changes.Status);
        Assert.StartsWith("Display · A", changes.Details[0], StringComparison.Ordinal);
        Assert.Equal(TriageOutcome.NoHardwareEvidence, report.Verdict.Outcome);
    }

    [Fact]
    public void WithoutABaselineTheChangeCheckIsNotApplicable()
        => Assert.Equal(CheckStatus.NotApplicable, Check(Run([]), "changes").Status);

    [Fact]
    public void DefenderDefinitionUpdatesAndDuplicateTitlesAreCollapsed()
    {
        WindowsUpdateEntry Entry(string title) => new(Now.AddDays(-1), title, "Install", "Succeeded", "0x00000000");
        var report = Run([], builder => builder.Updates = new WindowsUpdateHistory(Now,
            [Entry("Security Intelligence Update for Microsoft Defender Antivirus - KB2267602"), Entry("Driver X"), Entry("Driver X")], null, null, []));
        var updates = Check(report, "updates");
        Assert.Single(updates.Details);
        Assert.Contains("×2", updates.Details[0], StringComparison.Ordinal);
    }

    [Fact]
    public void ReportsSerializeAndEncodeUntrustedText()
    {
        var report = Run([Event("Microsoft-Windows-WHEA-Logger", 18, "Hardware", summary: "<script>alert(1)</script>", details: "Fatal hardware error")]);
        var html = TriageReportWriter.ToHtml(report);
        Assert.DoesNotContain("<script>alert(1)</script>", html, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", html, StringComparison.Ordinal);
        Assert.Contains("\"outcome\": \"HardwareEvidenceFound\"", TriageReportWriter.ToJson(report), StringComparison.Ordinal);
        Assert.Contains("VERDICT: HARDWARE, DRIVER OR FIRMWARE EVIDENCE FOUND", TriageReportWriter.ToText(report), StringComparison.Ordinal);
    }

    [Fact]
    public void RedactionRemovesComputerUserSerialAndSid()
    {
        var report = Run([Event("Microsoft-Windows-WHEA-Logger", 18, "Hardware", summary: @"Dump at C:\Users\alice.smith\CrashDumps on LAB-PC-07 owned by S-1-5-21-111-222-333-1001")])
            with { Machine = [new("System name", "LAB-PC-07"), new("Serial number", "ABC123"), new("Note", "LAB-PC-07 owner alice.smith")] };
        var redacted = TriageRedactor.Redact(report, "LAB-PC-07", "alice.smith");
        var text = TriageReportWriter.ToJson(redacted);
        Assert.DoesNotContain("LAB-PC-07", text, StringComparison.Ordinal);
        Assert.DoesNotContain("alice.smith", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ABC123", text, StringComparison.Ordinal);
        Assert.DoesNotContain("S-1-5-21-111", text, StringComparison.Ordinal);
        Assert.Contains("<user>", text, StringComparison.Ordinal);
    }

    [Fact]
    public void HandoffSeparatesHardwareLeadsFromSoftwareLeads()
    {
        var live = new[] { new DiagnosticFinding("Sustained CPU saturation", PressureState.Elevated, "x", []) };
        Assert.Contains("has not been checked", Handoff.Compose(null, live, null).Headline, StringComparison.Ordinal);
        var clean = Handoff.Compose(Run([]), live, "chrome.exe");
        Assert.Contains("software-side", clean.Headline, StringComparison.Ordinal);
        Assert.Contains("chrome.exe", clean.Detail, StringComparison.Ordinal);
        Assert.Contains("correlates", clean.Detail, StringComparison.Ordinal);
        var evidence = Handoff.Compose(Run([Event("Microsoft-Windows-WHEA-Logger", 18, "Hardware", details: "fatal")]), live, "chrome.exe");
        Assert.Contains("evidence exists", evidence.Headline, StringComparison.Ordinal);
        Assert.DoesNotContain("chrome.exe", evidence.Detail, StringComparison.Ordinal);
        Assert.Contains("No hardware evidence", Handoff.Compose(Run([]), [], null).Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void TruncatedSystemLogIsDisclosedOnEveryCleanCheckAndInTheVerdict()
    {
        var report = Run([], builder => builder.Truncated["System"] = Now.AddDays(-9));
        Assert.Equal(TriageOutcome.NoHardwareEvidence, report.Verdict.Outcome);
        Assert.Contains("only the last 9 of 30 days", report.Verdict.Detail, StringComparison.Ordinal);
        foreach (var id in new[] { "crashes", "whea", "storage", "memory", "graphics", "firmware", "drivers" })
            Assert.Contains("only the last 9 of 30 days", Check(report, id).Limit, StringComparison.Ordinal);
        Assert.DoesNotContain("only the last", Check(report, "app.faults").Limit ?? string.Empty, StringComparison.Ordinal);
        Assert.Null(Check(Run([], builder => builder.Truncated["System"] = Now.AddDays(-45)), "whea").Limit);
    }

    [Fact]
    public void UnreadableKernelDumpInTheWindowIsACrashNotAClearResult()
    {
        var report = Run([], builder => builder.Dumps = new CrashDumpInventory(Now, [new CrashDumpFile("System minidump", @"C:\Windows\Minidump\a.dmp", Now.AddDays(-3), 1000)], []));
        var crashes = Check(report, "crashes");
        Assert.Equal(CheckStatus.Found, crashes.Status);
        Assert.False(crashes.Minor);
        Assert.Contains("stop code unreadable", string.Join('\n', crashes.Details), StringComparison.Ordinal);
        var appDump = Run([], builder => builder.Dumps = new CrashDumpInventory(Now, [new CrashDumpFile("Current-user app dump", @"C:\x.dmp", Now.AddDays(-3), 1000)], []));
        Assert.Equal(CheckStatus.NotFound, Check(appDump, "crashes").Status);
    }

    [Fact]
    public void ManuallyTriggeredCrashIsOnlyMinor()
    {
        var report = Run([Event("Microsoft-Windows-Kernel-Power", 41, "Crashes & restarts", KernelPower41(0xE2), severity: "Critical")]);
        Assert.True(Check(report, "crashes").Minor);
        Assert.Equal(TriageOutcome.MinorFindings, report.Verdict.Outcome);
    }

    [Fact]
    public void PredictedDiskFailureIsSignificantStorageEvidence()
    {
        var report = Run([Event("disk", 52, "Drivers & storage", details: @"The driver has detected that device \Device\Harddisk0\DR0 has predicted that it will fail.")]);
        Assert.Equal(CheckStatus.Found, Check(report, "storage").Status);
        Assert.False(Check(report, "storage").Minor);
        Assert.Equal(TriageOutcome.HardwareEvidenceFound, report.Verdict.Outcome);
    }

    [Fact]
    public void DisabledGraphicsAdapterIsNotEvidence()
    {
        var adapter = new HardwareDevice("gpu/1", "Graphics", "Intel UHD", @"PCI\X", HardwareMatchConfidence.Verified, [],
            [new HardwareSignal("Device disabled", "This device is disabled; this may be intentional.", HardwareHealthState.Attention, "Win32_PnPEntity")]);
        var report = Run([], builder => builder.Hardware = new HardwareInventory(Now, [adapter], [], []));
        Assert.Equal(CheckStatus.NotFound, Check(report, "graphics").Status);
    }

    [Fact]
    public void UpdateCheckStatesTheWindowItActuallyUsed()
    {
        var report = Run([], builder => { builder.Days = 7; builder.Updates = new WindowsUpdateHistory(Now, [], null, null, []); });
        Assert.Contains("last 7 days", Check(report, "updates").Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void PlatformFindingsAreNotBlamedOnTheBusiestProcess()
    {
        var live = new[] { new DiagnosticFinding(@"Platform thermal limit · \_TZ.TZ00", PressureState.Elevated, "x", []) };
        var statement = Handoff.Compose(Run([]), live, "chrome.exe (PID 1)");
        Assert.Contains("platform", statement.Headline, StringComparison.Ordinal);
        Assert.DoesNotContain("chrome.exe", statement.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("software-side", statement.Headline, StringComparison.Ordinal);
    }

    [Fact]
    public void RedactionCoversEntraSidsDomainsAndDriverInstanceIdsButKeepsGuidance()
    {
        Assert.Equal("<sid>", TriageRedactor.Redact("S-1-12-1-1111-2222-3333-4444", "PC-01", "alice"));
        Assert.Equal("<computer>.<domain>.local", TriageRedactor.Redact("PC-01.CONTOSO.local", "PC-01", "alice", "CONTOSO"));
        Assert.Equal("Run as administrator", TriageRedactor.Redact("Run as administrator", "PC-01", "Administrator"));
        Assert.Equal(@"C:\Users\<user>\x", TriageRedactor.Redact(@"C:\Users\Administrator\x", "PC-01", "Administrator"));
        var drivers = new DriverInventory(Now, [new DriverRecord(@"USB\VID_1234&PID_5678\SERIAL0042", "PC-01 Bluetooth", "USB", "1.0", null, "Vendor", "a.inf", true)], []);
        var redacted = TriageRedactor.Redact(drivers, "PC-01", "alice");
        Assert.Equal(@"USB\VID_1234&PID_5678\<instance>", redacted.Drivers[0].DeviceId);
        Assert.Equal("<computer> Bluetooth", redacted.Drivers[0].Name);
    }

    [Theory]
    [InlineData(@"BTHENUM\DEV_542A4316F033\7&1C2D3E4F&0&BLUETOOTHDEVICE_542A4316F033", @"BTHENUM\DEV_<mac>\<instance>")]
    [InlineData(@"BTHLEDEVICE\{00001800-0000-1000-8000-00805F9B34FB}_DEV_VID&02046D_PID&B023_REV&0007_D1B6A62D8A85\8&2A&0&0001", @"BTHLEDEVICE\{00001800-0000-1000-8000-00805F9B34FB}_DEV_VID&02046D_PID&B023_REV&0007_<mac>\<instance>")]
    [InlineData(@"PCI\VEN_8086&DEV_A7A0&SUBSYS_00000000&REV_04\3&11583659&0&10", @"PCI\VEN_8086&DEV_A7A0&SUBSYS_00000000&REV_04\<instance>")]
    [InlineData(@"ROOT\SYSTEM", @"ROOT\SYSTEM")]
    public void DeviceIdsLoseSerialsAndMacsButKeepTheirType(string deviceId, string expected)
        => Assert.Equal(expected, TriageRedactor.RedactDeviceId(deviceId));

    [Fact]
    public void RedactionScrubsDeviceIdsInsideTextAndLeavesOrdinaryWordsAlone()
    {
        var text = TriageRedactor.Redact(@"The device USBSTOR\Disk&Ven_Kingston&Prod_DT\4C5300012508&0 was not migrated. Adapter 54:2A:43:16:F0:33.", "DESKTOP", "alice");
        Assert.DoesNotContain("4C5300012508", text, StringComparison.Ordinal);
        Assert.DoesNotContain("54:2A:43", text, StringComparison.Ordinal);
        Assert.Contains(@"USBSTOR\Disk&Ven_Kingston&Prod_DT\<instance>", text, StringComparison.Ordinal);
        Assert.Equal("No battery reported by Windows (desktop or battery not exposed).",
            TriageRedactor.Redact("No battery reported by Windows (desktop or battery not exposed).", "DESKTOP", "alice"));
        Assert.Equal("Remote Desktop Device Redirector Bus", TriageRedactor.Redact("Remote Desktop Device Redirector Bus", "DESKTOP", "alice"));
        Assert.Equal(@"Host <computer>, <computer>.corp.local and \\<computer>\share", TriageRedactor.Redact(@"Host DESKTOP, desktop.corp.local and \\desktop\share", "DESKTOP", "alice"));
    }

    [Fact]
    public void VendorStorageMiniportResetsReachTheStorageCheck()
    {
        Assert.Equal("Drivers & storage", ReliabilityAnalysis.Category("iaStorAC", 129));
        Assert.Null(ReliabilityAnalysis.Category("Microsoft-Windows-Hyper-V-VmSwitch", 129));
        var events = Enumerable.Range(0, 3).Select(_ => Event("iaStorAC", 129, "Drivers & storage", details: @"Reset to device, \Device\RaidPort0, was issued.")).ToArray();
        var report = Run(events);
        Assert.Equal(CheckStatus.Found, Check(report, "storage").Status);
        Assert.False(Check(report, "storage").Minor);
    }

    [Fact]
    public void UnmappedDiskCountersStillCountAndAreDescribedCorrectly()
    {
        var counter = new HardwareDevice("storage-counter/1", "Storage", "Unmapped storage counter 1", "Provider device ID 1", HardwareMatchConfidence.Unmapped, [],
            [new HardwareSignal("Uncorrected errors reported", "Historical counts: 500 read, 0 write.", HardwareHealthState.Attention, "MSFT_StorageReliabilityCounter (unmapped)")]);
        var report = Run([], builder => builder.Hardware = new HardwareInventory(Now, [counter], [], []));
        var storage = Check(report, "storage");
        Assert.Equal(CheckStatus.Found, storage.Status);
        Assert.False(storage.Minor);
        Assert.Contains("did not tie them to a specific disk", storage.Limit, StringComparison.Ordinal);
    }

    [Fact]
    public void OfflineDiskAloneIsMinor()
    {
        var disk = new HardwareDevice("disk/2", "Storage", "Disk 2", "x", HardwareMatchConfidence.Verified, [],
            [new HardwareSignal("Disk offline", "Windows reports this disk offline. It may be intentionally offline.", HardwareHealthState.Attention, "MSFT_Disk")]);
        Assert.True(Check(Run([], builder => builder.Hardware = new HardwareInventory(Now, [disk], [], [])), "storage").Minor);
    }

    [Fact]
    public void OneCrashIsCountedOnceAcrossEventAndUnreadableDump()
    {
        var crash = Event("Microsoft-Windows-Kernel-Power", 41, "Crashes & restarts", KernelPower41(0xE2), severity: "Critical", daysAgo: 2);
        var report = Run([crash], builder => builder.Dumps = new CrashDumpInventory(Now, [new CrashDumpFile("System memory dump", @"C:\Windows\MEMORY.DMP", crash.Timestamp.AddMinutes(1), 1000)], []));
        var crashes = Check(report, "crashes");
        Assert.StartsWith("1 bugcheck(s)", crashes.Summary, StringComparison.Ordinal);
        Assert.Contains("same crash as the record above", string.Join('\n', crashes.Details), StringComparison.Ordinal);
        Assert.True(crashes.Minor); // a deliberately triggered crash stays minor even when its dump is unreadable
    }

    [Fact]
    public void CorrectedMemoryWheaIsMinorButFatalIsNot()
    {
        var corrected = Run([Event("Microsoft-Windows-WHEA-Logger", 47, "Hardware", details: "A corrected hardware error has occurred. Component: Memory", severity: "Warning")]);
        Assert.True(Check(corrected, "memory").Minor);
        // The record level decides, whatever language the message is in.
        var fatal = Run([Event("Microsoft-Windows-WHEA-Logger", 18, "Hardware", details: "Ein schwerwiegender Hardwarefehler. memory", severity: "Error")]);
        Assert.False(Check(fatal, "memory").Minor);
        Assert.Contains("1 fatal/uncorrected", Check(fatal, "whea").Summary, StringComparison.Ordinal);
        Assert.False(Check(fatal, "whea").Minor);
    }

    [Fact]
    public void PartialDriverInventoryIsNeitherComparedNorTrusted()
    {
        DriverRecord Driver(string id) => new(id, id, "Net", "1.0", null, "Vendor", "a.inf", true);
        var report = Run([], builder =>
        {
            builder.Baseline = new DriverSnapshot(Now.AddDays(-1), "PC", null, null, null, [Driver("A"), Driver("B"), Driver("C")]);
            builder.Drivers = new DriverInventory(Now, [Driver("A")], []) { Complete = false };
        });
        Assert.Equal(CheckStatus.CouldNotCheck, Check(report, "changes").Status);
        Assert.Contains("incomplete", Check(report, "drivers").Limit, StringComparison.Ordinal);
    }

    [Fact]
    public void RequestedBaselineThatCouldNotBeReadIsReported()
    {
        var inputs = new TriageInputs(Now, 30, false) { Reliability = new(Now, [], ReadableLogs), BaselineRequested = "golden.json" };
        var changes = TriageEngine.Evaluate(inputs, "test").Checks.Single(check => check.Id == "changes");
        Assert.Equal(CheckStatus.CouldNotCheck, changes.Status);
        Assert.Contains("golden.json", changes.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void UpdateHistoryIsPartialOnlyWhenWindowsHoldsMore()
    {
        var entries = Enumerable.Range(0, 120).Select(i => new WindowsUpdateEntry(Now.AddDays(-1).AddMinutes(-i), $"Update {i}", "Install", "Succeeded", "0x0")).ToArray();
        var complete = Run([], builder => builder.Updates = new WindowsUpdateHistory(Now, entries, null, null, []) { TotalHistoryCount = 120 });
        Assert.DoesNotContain("Only the newest", Check(complete, "updates").Limit ?? string.Empty, StringComparison.Ordinal);
        var partial = Run([], builder => builder.Updates = new WindowsUpdateHistory(Now, entries, null, null, []) { TotalHistoryCount = 900 });
        Assert.Contains("Only the newest 120", Check(partial, "updates").Limit, StringComparison.Ordinal);
    }

    [Fact]
    public void HandoffCarriesLogCoverageInsteadOfClaimingTheFullWindow()
    {
        var report = Run([], builder => builder.Truncated["System"] = Now.AddDays(-3));
        var live = new[] { new DiagnosticFinding("Sustained CPU saturation", PressureState.Elevated, "x", []) };
        var statement = Handoff.Compose(report, live, "chrome.exe (PID 1)");
        Assert.Contains("only the last 3 of 30 days", statement.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("in the last 30 days", statement.Detail, StringComparison.Ordinal);
        Assert.Contains("Application event log", Check(Run([], builder => builder.Truncated["Application"] = Now.AddDays(-3)), "graphics").Limit, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Microsoft-Windows-Kernel-Processor-Power", 37, "Firmware & diagnostics")]
    [InlineData("Microsoft-Windows-Kernel-Processor-Power", 55, null)]
    [InlineData("Microsoft-Windows-MemoryDiagnostics-Results", 1202, "Firmware & diagnostics")]
    public void NewReliabilityCategoriesAreClassified(string provider, int id, string? category)
        => Assert.Equal(category, ReliabilityAnalysis.Category(provider, id));
}
