using System.Globalization;
using System.Text.RegularExpressions;
using Skald.Core.Models;

namespace Skald.Triage;

// Turns collected Windows records into per-area checks and one verdict. Every check states what it looked at; "not found" means
// "Windows recorded nothing", never "the hardware was tested and is healthy".
public static partial class TriageEngine
{
    private const string Hw = "hardware";
    private const int MaxLines = 8;

    public static readonly IReadOnlyList<string> NotCovered =
    [
        "No stress test or active diagnostic is run. This scan reads what Windows has already recorded; for a conclusive hardware test, run the manufacturer's pre-boot diagnostics.",
        "Live thermal and power-limit behavior is not sampled here. Use Investigate slowness while the problem is happening.",
        "Intermittent faults that left no Windows record, events older than the scan window, and cleared or rotated event logs.",
        "Which kernel driver caused a crash. Stop codes narrow the area; only a dump analysis (WinDbg) names the module."
    ];

    public static TriageReport Evaluate(TriageInputs inputs, string toolVersion)
    {
        var cutoff = inputs.Now.AddDays(-inputs.WindowDays);
        var events = inputs.Reliability?.Events.Where(item => item.Timestamp >= cutoff && item.Timestamp <= inputs.Now).ToArray() ?? [];
        var faults = events.Select(AppFaultParser.Parse).OfType<AppFault>().ToArray();
        var incidents = Incidents(events);
        var dumpCodes = DumpCodes(inputs, cutoff);

        var checks = new List<TriageCheck>
        {
            Crashes(inputs, incidents, dumpCodes),
            Whea(inputs, events, incidents),
            Storage(inputs, events, faults),
            Memory(inputs, events, incidents, dumpCodes),
            Graphics(inputs, events, incidents, dumpCodes, faults),
            Firmware(inputs, events),
            Devices(inputs),
            Drivers(inputs, events),
            Battery(inputs),
            AppFaultCheck(inputs, faults),
            RestartPending(inputs),
            DiskSpace(inputs),
            RecentChanges(inputs),
            DriverChanges(inputs),
            FirmwareAge(inputs)
        };

        var coverageNote = ApplyLogCoverage(inputs, cutoff, checks);
        var notCovered = new List<string>(NotCovered);
        if (!inputs.Elevated) notCovered.Add("Not run as administrator: kernel dump files and some storage health counters can be unreadable. Checks that were affected say so.");
        if (coverageNote is not null) notCovered.Add(coverageNote);

        var status = new List<string>(inputs.SourceStatus);
        return new TriageReport(inputs.Now, inputs.WindowDays, inputs.Elevated, toolVersion, inputs.Machine,
            Verdict(checks, inputs.WindowDays, coverageNote), checks, notCovered, status);
    }

    private static readonly string[] SystemLogChecks = ["crashes", "whea", "storage", "memory", "graphics", "firmware", "drivers"];

    // When the scan cap stopped short of the window, a "nothing found" only covers the newer part of it. Say so on every affected check.
    private static string? ApplyLogCoverage(TriageInputs inputs, DateTimeOffset cutoff, List<TriageCheck> checks)
    {
        if (inputs.Reliability is not { } history) return null;
        var notes = new List<string>();
        // Graphics also reads WER LiveKernelEvent and application faults from the Application log.
        foreach (var (log, ids) in new[] { ("System", SystemLogChecks), ("Application", new[] { "app.faults", "graphics" }) })
        {
            if (!history.TruncatedAt.TryGetValue(log, out var oldest) || oldest <= cutoff) continue;
            var days = Math.Max(0, (inputs.Now - oldest).TotalDays);
            var note = string.Create(CultureInfo.InvariantCulture,
                $"The {log} event log is busy: the scan cap was reached at records from {Stamp(oldest)}, so only the last {days:F0} of {inputs.WindowDays} days were examined.");
            notes.Add(note);
            for (var i = 0; i < checks.Count; i++)
                if (ids.Contains(checks[i].Id) && checks[i].Status == CheckStatus.NotFound)
                    checks[i] = checks[i] with { Limit = checks[i].Limit is null ? note : note + " " + checks[i].Limit };
        }
        return notes.Count == 0 ? null : string.Join(" ", notes);
    }

    public static TriageVerdict Verdict(IReadOnlyList<TriageCheck> checks, int days, string? coverageNote = null)
    {
        var hardware = checks.Where(check => check.Domain == CheckDomain.HardwareDriverFirmware).ToArray();
        var significant = hardware.Where(check => check.Status == CheckStatus.Found && !check.Minor).Select(check => check.Title).ToArray();
        var minor = hardware.Where(check => check.Status == CheckStatus.Found && check.Minor).Select(check => check.Title).ToArray();
        var ran = hardware.Count(check => check.Status is CheckStatus.Found or CheckStatus.NotFound);
        var skipped = hardware.Where(check => check.Status == CheckStatus.CouldNotCheck).ToArray();
        var software = checks.FirstOrDefault(check => check.Id == "app.faults" && check.Status == CheckStatus.Found);

        if (significant.Length > 0)
            return new(TriageOutcome.HardwareEvidenceFound, "Hardware, driver or firmware evidence found",
                $"Look at these first: {string.Join("; ", significant)}."
                + (minor.Length > 0 ? $" Minor findings also noted: {string.Join("; ", minor)}." : string.Empty)
                + " The evidence comes from Windows' own records and shows where to look; it does not by itself identify a failed part.", significant, coverageNote);

        if (hardware.Any(check => check.Id == "crashes" && check.Status == CheckStatus.CouldNotCheck))
            return new(TriageOutcome.Incomplete, "Hardware check incomplete",
                "The System event log could not be read, so crashes and unexpected restarts could not be assessed. Run as administrator or fix event log access, then scan again.", []);

        var coverage = $"{ran} checks ran" + (skipped.Length > 0 ? $", {skipped.Length} could not run: {string.Join(", ", skipped.Select(check => check.Title))}" : string.Empty);
        var caveat = "This is the absence of significant evidence in Windows' records, not a hardware test. If the symptom persists, reproduce it with Record and Mark Problem, and run the manufacturer's pre-boot diagnostics before closing hardware out."
            + (coverageNote is null ? string.Empty : " " + coverageNote)
            + (software is null ? string.Empty : $" Application-side evidence exists: {software.Summary}");
        if (minor.Length > 0)
            return new(TriageOutcome.MinorFindings, "Minor findings only",
                $"Windows recorded small signals ({string.Join("; ", minor)}) of the kind that also appear on healthy machines. Check them against the reported symptom before treating hardware as a suspect ({coverage}; last {days} days). " + caveat, minor, coverageNote);
        return new(TriageOutcome.NoHardwareEvidence, "No hardware, driver or firmware evidence found",
            $"Windows recorded no hardware-leaning crashes, WHEA reports, storage errors, graphics driver resets, device problems or firmware throttling in the last {days} days ({coverage}). " + caveat, [], coverageNote);
    }

    // ---- Crashes -------------------------------------------------------------------------------------------------------

    private sealed record Incident(DateTimeOffset Time, uint? Bugcheck, bool LongPowerButton, bool Whea);

    private static List<Incident> Incidents(IReadOnlyList<ReliabilityEvent> events)
    {
        var relevant = events.Where(item => item.Category == "Crashes & restarts").OrderBy(item => item.Timestamp).ToArray();
        var incidents = new List<Incident>();
        var group = new List<ReliabilityEvent>();
        foreach (var item in relevant)
        {
            if (group.Count > 0 && item.Timestamp - group[^1].Timestamp > TimeSpan.FromMinutes(10)) { incidents.Add(Summarize(group)); group.Clear(); }
            group.Add(item);
        }
        if (group.Count > 0) incidents.Add(Summarize(group));
        return incidents;
    }

    private static Incident Summarize(List<ReliabilityEvent> group)
    {
        uint? code = null;
        var longPress = false;
        var whea = false;
        foreach (var item in group)
        {
            var data = EventData.Named(item.Raw);
            if (data.TryGetValue("BugcheckCode", out var value) && Bugchecks.TryParse(value, out var parsed) && parsed != 0) code ??= parsed;
            if (data.TryGetValue("LongPowerButtonPressDetected", out var pressed) && pressed.Equals("true", StringComparison.OrdinalIgnoreCase)) longPress = true;
            if (data.TryGetValue("WHEABootErrorCount", out var count) && int.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out var errors) && errors > 0) whea = true;
            if (code is null && BugcheckText().Match(item.Details) is { Success: true } match && Bugchecks.TryParse(match.Groups[1].Value, out var fromText) && fromText != 0) code = fromText;
        }
        return new Incident(group[0].Timestamp, code, longPress, whea);
    }

    private static List<(DateTimeOffset Time, string Kind, DumpHeaderInfo Header)> DumpCodes(TriageInputs inputs, DateTimeOffset cutoff)
        => inputs.Dumps is null ? [] : inputs.Dumps.Files.Where(file => file.ModifiedAt >= cutoff && inputs.DumpHeaders.ContainsKey(file.Path))
            .Select(file => (file.ModifiedAt, file.Kind, inputs.DumpHeaders[file.Path])).ToList();

    private static bool Near(DateTimeOffset first, DateTimeOffset second) => Math.Abs((first - second).TotalHours) < 1;

    private static TriageCheck Crashes(TriageInputs inputs, List<Incident> incidents, List<(DateTimeOffset Time, string Kind, DumpHeaderInfo Header)> dumps)
    {
        const string title = "Crashes & unexpected restarts";
        if (!LogReadable(inputs.Reliability, "System"))
            return new("crashes", title, CheckDomain.HardwareDriverFirmware, CheckStatus.CouldNotCheck, "The System event log could not be read.", [],
                "Check event log access (some locked-down images block it) or run as administrator.");

        var limit = default(string);
        // A kernel dump file proves a bugcheck happened even when its header cannot be read and no event record survived.
        var unreadable = inputs.Dumps?.Files.Where(file => file.ModifiedAt >= inputs.Now.AddDays(-inputs.WindowDays)
            && file.Kind.StartsWith("System", StringComparison.Ordinal) && !inputs.DumpHeaders.ContainsKey(file.Path)).ToArray() ?? [];
        if (unreadable.Length > 0) limit = $"{unreadable.Length} kernel dump file(s) in the window could not be read, so their stop codes are unknown (run as administrator to read them).";
        else if (inputs.Dumps?.SourceStatus.Any(item => item.StartsWith("System", StringComparison.Ordinal) && item.Contains("unavailable", StringComparison.OrdinalIgnoreCase)) == true)
            limit = "The kernel dump folders could not be read without administrator rights, so dump stop codes were not checked; event log records were.";
        if (incidents.Count == 0 && dumps.Count == 0 && unreadable.Length == 0)
            return new("crashes", title, CheckDomain.HardwareDriverFirmware, CheckStatus.NotFound,
                $"No bugchecks or unexpected shutdowns recorded in the last {inputs.WindowDays} days.", [], limit);

        // One crash can leave an event record, a readable dump and an unreadable dump; within an hour they are the same crash.
        var bugchecks = incidents.Where(item => item.Bugcheck is not null).ToArray();
        var dumpOnly = dumps.Where(dump => !incidents.Any(item => Near(item.Time, dump.Time))).ToArray();
        var unreadableOnly = unreadable.Where(file => !incidents.Any(item => Near(item.Time, file.ModifiedAt)) && !dumps.Any(dump => Near(dump.Time, file.ModifiedAt))).ToArray();
        var silent = incidents.Where(item => item.Bugcheck is null && !dumps.Any(dump => Near(dump.Time, item.Time)) && !unreadable.Any(file => Near(file.ModifiedAt, item.Time))).ToArray();
        var crashCount = incidents.Count - silent.Length + dumpOnly.Length + unreadableOnly.Length;

        var details = new List<string>();
        foreach (var item in incidents.OrderByDescending(item => item.Time))
        {
            if (item.Bugcheck is { } code)
            {
                var info = Bugchecks.Describe(code);
                details.Add($"{Stamp(item.Time)} · bugcheck {info.Display} — {info.LeanText}");
            }
            else if (!silent.Contains(item)) details.Add($"{Stamp(item.Time)} · unexpected shutdown with a kernel dump written: a bugcheck happened (stop code in the dump line below, if readable)");
            else details.Add($"{Stamp(item.Time)} · unexpected shutdown, no bugcheck recorded" + (item.LongPowerButton ? " (long power-button press detected: typically a hung machine forced off)" : string.Empty)
                + (item.Whea ? " (WHEA errors were logged at boot)" : string.Empty));
        }
        var lines = Cap(details);
        foreach (var dump in dumps.OrderByDescending(item => item.Time).Take(MaxLines))
            lines.Add($"{Stamp(dump.Time)} · {dump.Kind} header: {dump.Header.Bugcheck.Display} — {dump.Header.Bugcheck.LeanText}");
        foreach (var file in unreadable.OrderByDescending(file => file.ModifiedAt).Take(MaxLines))
            lines.Add($"{Stamp(file.ModifiedAt)} · {file.Kind} written (stop code unreadable)"
                + (unreadableOnly.Contains(file) ? ": a bugcheck happened at about this time." : ": same crash as the record above."));

        var knownCodes = bugchecks.Select(item => Bugchecks.Describe(item.Bugcheck!.Value))
            .Concat(dumps.Where(dump => !bugchecks.Any(item => Near(item.Time, dump.Time))).Select(dump => dump.Header.Bugcheck)).ToArray();
        var leans = knownCodes.Select(code => code.Lean).Where(lean => lean is not BugcheckLean.General and not BugcheckLean.Manual)
            .GroupBy(lean => lean).OrderByDescending(group => group.Count()).Select(group => $"{group.Key} ×{group.Count()}").ToArray();
        var summary = $"{crashCount} bugcheck(s) and {silent.Length} unexpected shutdown(s) without a bugcheck in the last {inputs.WindowDays} days"
            + (leans.Length > 0 ? $". Stop codes point toward: {string.Join(", ", leans)}" : string.Empty) + ".";
        // Minor: one unexplained shutdown with no other sign, or only crashes that were deliberately triggered (0xE2).
        var manualOnly = knownCodes.Length > 0 && knownCodes.All(code => code.Lean == BugcheckLean.Manual)
            && silent.Length == 0 && unreadableOnly.Length == 0 && incidents.All(item => item.Bugcheck is not null || dumps.Any(dump => Near(dump.Time, item.Time)));
        var minor = manualOnly || (crashCount == 0 && silent.Length == 1 && !silent[0].LongPowerButton && !silent[0].Whea);
        return new("crashes", title, CheckDomain.HardwareDriverFirmware, CheckStatus.Found, summary, lines,
            limit ?? "Stop codes and shutdown records narrow the area. They do not prove a failed part; unexpected shutdowns also follow power loss and forced power-offs.", minor);
    }

    // ---- WHEA ----------------------------------------------------------------------------------------------------------

    private static TriageCheck Whea(TriageInputs inputs, IReadOnlyList<ReliabilityEvent> events, List<Incident> incidents)
    {
        const string title = "WHEA hardware error reports";
        if (!LogReadable(inputs.Reliability, "System"))
            return new("whea", title, CheckDomain.HardwareDriverFirmware, CheckStatus.CouldNotCheck, "The System event log could not be read.", []);
        var reports = events.Where(item => item.Provider.Contains("WHEA", StringComparison.OrdinalIgnoreCase)).ToArray();
        var silentBoot = incidents.Count(item => item.Whea);
        if (reports.Length == 0 && silentBoot == 0)
            return new("whea", title, CheckDomain.HardwareDriverFirmware, CheckStatus.NotFound, $"No WHEA reports in the last {inputs.WindowDays} days.", []);
        var fatal = reports.Count(item => WheaFatal(item) == true);
        var corrected = reports.Count(item => WheaFatal(item) == false);
        var details = Cap(reports.GroupBy(item => item.EventId).OrderByDescending(group => group.Count()).Select(group =>
        {
            var latest = group.MaxBy(item => item.Timestamp)!;
            return $"Event {group.Key} ×{group.Count()}, latest {Stamp(latest.Timestamp)}: {Text(latest)}";
        }));
        if (silentBoot > 0) details.Add($"{silentBoot} unexpected-shutdown record(s) also noted WHEA errors at the following boot.");
        return new("whea", title, CheckDomain.HardwareDriverFirmware, CheckStatus.Found,
            $"{reports.Length} WHEA report(s): {fatal} fatal/uncorrected, {corrected} corrected, {reports.Length - fatal - corrected} unclassified.", details,
            "WHEA names the error source (CPU, memory controller, PCIe), which is not necessarily the replaceable failing part. Corrected errors are normal in small numbers.",
            Minor: fatal == 0 && reports.Length < 10 && silentBoot == 0);
    }

    // ---- Storage -------------------------------------------------------------------------------------------------------

    private static TriageCheck Storage(TriageInputs inputs, IReadOnlyList<ReliabilityEvent> events, AppFault[] faults)
    {
        const string title = "Storage (disks and controllers)";
        var systemReadable = LogReadable(inputs.Reliability, "System");
        if (!systemReadable && inputs.Hardware is null)
            return new("storage", title, CheckDomain.HardwareDriverFirmware, CheckStatus.CouldNotCheck, "Neither the System event log nor disk health data could be read.", []);

        var details = new List<string>();
        var linked = events.Select(item => (Item: item, Link: StorageIncidentAnalysis.Link(item))).Where(pair => pair.Link is not null).ToArray();
        var strongEvents = linked.Length >= 3 || linked.Any(pair => pair.Link!.Description is "Bad block report" or "Controller error report" or "Disk surprise removal" or "Predicted failure (SMART)");
        foreach (var group in linked.GroupBy(pair => pair.Link!.Description).OrderByDescending(group => group.Count()))
        {
            var latest = group.MaxBy(pair => pair.Item.Timestamp)!;
            var disk = group.Select(pair => pair.Link!.DiskNumber).Where(number => number.HasValue).Distinct().ToArray() is { Length: 1 } one ? $" (disk {one[0]})" : string.Empty;
            details.Add($"{group.Key} ×{group.Count()}{disk}, latest {Stamp(latest.Item.Timestamp)}");
        }
        var eventLines = details.Count;
        var deviceLines = 0;
        var strongDevice = false;
        foreach (var device in inputs.Hardware?.Devices.Where(item => item.Category == "Storage") ?? [])
            foreach (var signal in device.Signals.Where(item => item.State >= HardwareHealthState.Attention && !item.Source.Contains("· event", StringComparison.Ordinal)))
            {
                details.Add($"{device.Name}: {signal.Title} — {signal.Detail}");
                deviceLines++;
                // An offline disk is often deliberate (SAN policy, a spare); it is shown but is not strong evidence on its own.
                if (signal.Title != "Disk offline") strongDevice = true;
            }
        var fileSystem = events.Where(item => item.Provider.Contains("Ntfs", StringComparison.OrdinalIgnoreCase) && item.EventId is 55 or 98 or 140).ToArray();
        if (fileSystem.Length > 0)
            details.Add($"File system corruption or repair-needed reports (Ntfs 55/98/140) ×{fileSystem.Length}, latest {Stamp(fileSystem.Max(item => item.Timestamp))}. These also follow forced power-offs.");
        var inPage = faults.Count(fault => ExceptionCodes.Describe(fault.ExceptionCode).Contains("in-page", StringComparison.OrdinalIgnoreCase));
        if (inPage > 0) details.Add($"{inPage} application crash(es) were in-page I/O errors (0xC0000006), which can follow a failed read from a disk or network file.");

        var limits = new List<string>();
        if (!systemReadable) limits.Add("The System event log could not be read, so storage driver reports were not checked.");
        if (inputs.Hardware is null) limits.Add("Disk health data (provider status, wear, error counters) could not be read; only event log reports were checked.");
        else if (!inputs.Hardware.Devices.Any(item => item.Facts.Any(fact => fact.Label == "Reliability-counter match")))
            limits.Add(inputs.Hardware.Devices.Any(item => item.Key.StartsWith("storage-counter/", StringComparison.Ordinal))
                ? "Disk wear and error counters were read but Windows did not tie them to a specific disk; they are listed as unmapped counters."
                : "Disk wear and error counters were not exposed on this system (often needs administrator rights or driver support), so only Windows' health status and event reports were checked.");
        var limit = limits.Count == 0 ? null : string.Join(" ", limits);
        if (details.Count == 0)
            return new("storage", title, CheckDomain.HardwareDriverFirmware, CheckStatus.NotFound, "No storage errors, resets or health warnings recorded.", [], limit);
        return new("storage", title, CheckDomain.HardwareDriverFirmware, CheckStatus.Found,
            $"{eventLines + inPage + (fileSystem.Length > 0 ? 1 : 0)} storage report group(s) and {deviceLines} disk health signal(s).", Cap(details), limit ?? "Event reports link to a disk only when the message names it; a report is a timing clue, not proof the drive is failing.",
            Minor: !strongDevice && !strongEvents);
    }

    // ---- Memory --------------------------------------------------------------------------------------------------------

    private static TriageCheck Memory(TriageInputs inputs, IReadOnlyList<ReliabilityEvent> events, List<Incident> incidents,
        List<(DateTimeOffset Time, string Kind, DumpHeaderInfo Header)> dumps)
    {
        const string title = "Memory (RAM)";
        if (!LogReadable(inputs.Reliability, "System"))
            return new("memory", title, CheckDomain.HardwareDriverFirmware, CheckStatus.CouldNotCheck, "The System event log could not be read.", []);
        var details = new List<string>();
        var diagnostics = events.Where(item => item.Provider.Contains("MemoryDiagnostics", StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var item in diagnostics.Where(item => item.EventId == 1202)) details.Add($"{Stamp(item.Timestamp)} · Windows Memory Diagnostic reported hardware errors.");
        var memoryStops = incidents.Where(item => item.Bugcheck is { } code && Bugchecks.Describe(code).Lean == BugcheckLean.Memory).ToArray();
        foreach (var item in memoryStops) details.Add($"{Stamp(item.Time)} · memory-leaning bugcheck {Bugchecks.Describe(item.Bugcheck!.Value).Display}");
        foreach (var dump in dumps.Where(dump => dump.Header.Bugcheck.Lean == BugcheckLean.Memory && !memoryStops.Any(item => Math.Abs((item.Time - dump.Time).TotalHours) < 1)))
            details.Add($"{Stamp(dump.Time)} · {dump.Kind} header: memory-leaning bugcheck {dump.Header.Bugcheck.Display}");
        var significant = details.Count > 0;
        var wheaMemory = events.Where(item => item.Provider.Contains("WHEA", StringComparison.OrdinalIgnoreCase) && item.Details.Contains("memory", StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var item in wheaMemory.Take(3))
            details.Add($"{Stamp(item.Timestamp)} · WHEA report mentioning memory: {item.Summary}");
        if (wheaMemory.Any(item => WheaFatal(item) == true)) significant = true;

        var ranClean = diagnostics.Any(item => item.EventId == 1201);
        var limit = ranClean ? "A Windows Memory Diagnostic run completed without errors in this window, but it is a short test; intermittent faults can pass it."
            : "No Windows Memory Diagnostic result is on record, so memory has not been actively tested by Windows. Absence of memory errors here is weak evidence.";
        return details.Count == 0
            ? new("memory", title, CheckDomain.HardwareDriverFirmware, CheckStatus.NotFound, "No memory errors, memory-leaning bugchecks or failed memory diagnostics recorded.", [], limit)
            : new("memory", title, CheckDomain.HardwareDriverFirmware, CheckStatus.Found, $"{details.Count} memory-related record(s).", Cap(details), limit, Minor: !significant);
    }

    // ---- Graphics ------------------------------------------------------------------------------------------------------

    private static bool IsGraphicsProvider(string provider) => provider.Equals("Display", StringComparison.OrdinalIgnoreCase)
        || provider.Contains("nvlddmkm", StringComparison.OrdinalIgnoreCase) || provider.Contains("amdkmd", StringComparison.OrdinalIgnoreCase)
        || provider.Contains("igfx", StringComparison.OrdinalIgnoreCase);

    private static TriageCheck Graphics(TriageInputs inputs, IReadOnlyList<ReliabilityEvent> events, List<Incident> incidents,
        List<(DateTimeOffset Time, string Kind, DumpHeaderInfo Header)> dumps, AppFault[] faults)
    {
        const string title = "Graphics driver and GPU";
        if (!LogReadable(inputs.Reliability, "System"))
            return new("graphics", title, CheckDomain.HardwareDriverFirmware, CheckStatus.CouldNotCheck, "The System event log could not be read.", []);
        var details = new List<string>();
        var resets = events.Where(item => IsGraphicsProvider(item.Provider)).ToArray();
        if (resets.Length > 0)
        {
            var latest = resets.MaxBy(item => item.Timestamp)!;
            details.Add($"{resets.Length} display driver report(s), latest {Stamp(latest.Timestamp)}: {Text(latest)}");
        }
        var kernelHangs = events.Where(item => item.Provider.Equals("Windows Error Reporting", StringComparison.OrdinalIgnoreCase)
            && item.Component.StartsWith("LiveKernelEvent", StringComparison.OrdinalIgnoreCase)
            && (item.Component.EndsWith("· 141", StringComparison.OrdinalIgnoreCase) || item.Component.EndsWith("· 117", StringComparison.OrdinalIgnoreCase))).ToArray();
        if (kernelHangs.Length > 0) details.Add($"{kernelHangs.Length} GPU hang report(s) (LiveKernelEvent 117/141), latest {Stamp(kernelHangs.Max(item => item.Timestamp))}.");
        var stops = incidents.Where(item => item.Bugcheck is { } code && Bugchecks.Describe(code).Lean == BugcheckLean.Graphics).ToArray();
        foreach (var item in stops) details.Add($"{Stamp(item.Time)} · graphics-leaning bugcheck {Bugchecks.Describe(item.Bugcheck!.Value).Display}");
        foreach (var dump in dumps.Where(dump => dump.Header.Bugcheck.Lean == BugcheckLean.Graphics && !stops.Any(item => Math.Abs((item.Time - dump.Time).TotalHours) < 1)))
            details.Add($"{Stamp(dump.Time)} · {dump.Kind} header: graphics-leaning bugcheck {dump.Header.Bugcheck.Display}");
        var graphicsCrashes = faults.Where(fault => fault.Origin == ModuleOrigin.GraphicsDriver).ToArray();
        foreach (var group in graphicsCrashes.GroupBy(fault => (fault.App.ToLowerInvariant(), fault.Module)))
            details.Add($"{group.Count()} crash(es) of {group.First().App} inside graphics driver module {group.Key.Module}.");
        var deviceSignals = 0;
        foreach (var device in inputs.Hardware?.Devices.Where(item => item.Category == "Graphics") ?? [])
            // A disabled adapter (code 22) is often deliberate, for example an iGPU turned off on a hybrid laptop; Devices treats it the same way.
            foreach (var signal in device.Signals.Where(item => item.State >= HardwareHealthState.Attention && item.Title != "Device disabled"))
            {
                details.Add($"{device.Name}: {signal.Title} — {signal.Detail}");
                deviceSignals++;
            }
        if (details.Count == 0)
            return new("graphics", title, CheckDomain.HardwareDriverFirmware, CheckStatus.NotFound, "No display driver resets, GPU hang reports or graphics-leaning crashes recorded.", []);
        var parts = new List<string>();
        var dumpStops = dumps.Count(dump => dump.Header.Bugcheck.Lean == BugcheckLean.Graphics);
        if (resets.Length > 0) parts.Add($"display driver report(s) ×{resets.Length}");
        if (kernelHangs.Length > 0) parts.Add($"GPU hang report(s) ×{kernelHangs.Length}");
        if (stops.Length + dumpStops > 0) parts.Add("graphics-leaning bugcheck(s)");
        if (graphicsCrashes.Length > 0) parts.Add($"app crash(es) inside graphics driver modules ×{graphicsCrashes.Length}");
        if (deviceSignals > 0) parts.Add("adapter problem reported by Windows");
        var strong = resets.Length >= 3 || kernelHangs.Length > 0 || stops.Length + dumpStops > 0 || graphicsCrashes.Length >= 2 || deviceSignals > 0;
        return new("graphics", title, CheckDomain.HardwareDriverFirmware, CheckStatus.Found, string.Join(", ", parts) + ".", Cap(details),
            "Driver resets usually point to the graphics driver or its settings first; the GPU itself is a later suspect.", Minor: !strong);
    }

    // ---- Firmware ------------------------------------------------------------------------------------------------------

    private static TriageCheck Firmware(TriageInputs inputs, IReadOnlyList<ReliabilityEvent> events)
    {
        const string title = "Firmware limits (processor throttling)";
        if (!LogReadable(inputs.Reliability, "System"))
            return new("firmware", title, CheckDomain.HardwareDriverFirmware, CheckStatus.CouldNotCheck, "The System event log could not be read.", []);
        var limited = events.Where(item => item.Category == "Firmware & diagnostics" && item.Provider.Contains("Processor-Power", StringComparison.OrdinalIgnoreCase)).ToArray();
        var limit = "Reads Windows' record that firmware limited a processor. Live clock and thermal behavior is only visible while sampling (Investigate slowness).";
        if (limited.Length == 0)
            return new("firmware", title, CheckDomain.HardwareDriverFirmware, CheckStatus.NotFound, "Windows recorded no firmware limit on any processor.", [], limit);
        var latest = limited.MaxBy(item => item.Timestamp)!;
        return new("firmware", title, CheckDomain.HardwareDriverFirmware, CheckStatus.Found,
            $"Processor speed was limited by system firmware {limited.Length} time(s), latest {Stamp(latest.Timestamp)}.", [Text(latest)],
            "Firmware limits come from the BIOS or embedded controller (thermal, power or battery policy). Occasional reports are routine on laptops; frequent ones point at cooling, power policy or BIOS.",
            Minor: limited.Length < 10);
    }

    // ---- Devices and drivers -------------------------------------------------------------------------------------------

    private static TriageCheck Devices(TriageInputs inputs)
    {
        const string title = "Device Manager problems";
        if (inputs.System is not { DevicesAvailable: true } system)
            return new("devices", title, CheckDomain.HardwareDriverFirmware, CheckStatus.CouldNotCheck, "Device status could not be read.", []);
        var problems = system.DeviceProblems.Where(item => !item.IsDisabled).ToArray();
        var disabled = system.DeviceProblems.Count(item => item.IsDisabled);
        var note = disabled > 0 ? $"{disabled} disabled device(s) not counted; disabling may be intentional." : null;
        return problems.Length == 0
            ? new("devices", title, CheckDomain.HardwareDriverFirmware, CheckStatus.NotFound, "No present device reports a Device Manager problem code.", [], note)
            : new("devices", title, CheckDomain.HardwareDriverFirmware, CheckStatus.Found, $"{problems.Length} present device(s) report a problem.",
                Cap(problems.Select(item => item.Description)), note ?? "A problem code means Windows could not start or configure the device; it is most often a driver issue.",
                Minor: problems.All(item => item.Name.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase)));
    }

    // Generic user-mode driver reflector loads fail for any unplugged UMDF device, so they say nothing about a fault.
    private static readonly string[] BenignDriverNames = [@"\Driver\WUDFRd", @"\Driver\WudfPf", @"\Driver\WUDFWpdMtp", @"\Driver\WpdUpFltr"];

    private static TriageCheck Drivers(TriageInputs inputs, IReadOnlyList<ReliabilityEvent> events)
    {
        const string title = "Drivers (signing and load failures)";
        var details = new List<string>();
        string? limit = null;
        var unsignedCount = 0;
        if (inputs.Drivers is null) limit = "The driver inventory could not be read, so signing was not checked.";
        else
        {
            if (!inputs.Drivers.Complete) limit = $"The driver inventory was incomplete ({inputs.Drivers.Drivers.Count} drivers read), so unsigned drivers may be missed.";
            var unsigned = inputs.Drivers.Drivers.Where(driver => driver.IsSigned == false).ToArray();
            unsignedCount = unsigned.Length;
            if (unsigned.Length > 0) details.Add($"{unsigned.Length} installed driver(s) are not signed: " + string.Join("; ", unsigned.Take(MaxLines).Select(driver => $"{driver.Name} ({driver.Provider} {driver.Version})")) + (unsigned.Length > MaxLines ? "; …" : string.Empty));
        }
        var failures = events.Where(item => item.Category == "Drivers & storage"
            && (item.Provider.Contains("Kernel-PnP", StringComparison.OrdinalIgnoreCase) || item.Provider.Contains("DriverFrameworks", StringComparison.OrdinalIgnoreCase))).ToArray();
        bool Benign(ReliabilityEvent item) => BenignDriverNames.Any(name => item.Summary.Contains(name, StringComparison.OrdinalIgnoreCase));
        var ignored = failures.Count(Benign);
        var counted = failures.Where(item => !Benign(item)).ToArray();
        foreach (var group in counted.GroupBy(item => $"{item.Provider}|{item.EventId}").OrderByDescending(group => group.Count()).Take(MaxLines))
        {
            var latest = group.MaxBy(item => item.Timestamp)!;
            details.Add($"{latest.Provider} event {latest.EventId} ×{group.Count()}, latest {Stamp(latest.Timestamp)}: {Text(latest)}");
        }
        if (ignored > 0) limit = (limit is null ? string.Empty : limit + " ") + $"{ignored} generic UMDF reflector load warning(s) (WUDFRd and similar) were ignored; they appear on healthy machines whenever a device is unplugged.";
        if (!LogReadable(inputs.Reliability, "System"))
        {
            if (inputs.Drivers is null)
                return new("drivers", title, CheckDomain.HardwareDriverFirmware, CheckStatus.CouldNotCheck, "Neither the driver inventory nor the System event log could be read.", []);
            limit = (limit is null ? string.Empty : limit + " ") + "The System event log could not be read, so driver load failures were not checked.";
        }
        if (details.Count == 0)
            return new("drivers", title, CheckDomain.HardwareDriverFirmware, CheckStatus.NotFound, "No unsigned drivers or driver load/start failures recorded.", [], limit);
        var framework = counted.Any(item => item.Provider.Contains("DriverFrameworks", StringComparison.OrdinalIgnoreCase));
        return new("drivers", title, CheckDomain.HardwareDriverFirmware, CheckStatus.Found, $"{details.Count} driver finding(s).", Cap(details),
            limit ?? "Some warnings from Kernel-PnP and driver frameworks are benign when a device is absent; compare against the device it names.",
            Minor: unsignedCount == 0 && !framework);
    }

    // ---- Battery -------------------------------------------------------------------------------------------------------

    private static TriageCheck Battery(TriageInputs inputs)
    {
        const string title = "Battery health";
        if (inputs.Hardware is null)
            return new("battery", title, CheckDomain.HardwareDriverFirmware, CheckStatus.CouldNotCheck, "Hardware inventory could not be read.", []);
        var batteries = inputs.Hardware.Devices.Where(item => item.Category == "Battery").ToArray();
        if (batteries.Length == 0)
            return new("battery", title, CheckDomain.HardwareDriverFirmware, CheckStatus.NotApplicable, "No battery reported by Windows (desktop or battery not exposed).", []);
        var worn = batteries.Where(item => item.Signals.Any(signal => signal.Title == "Reduced full-charge capacity")).ToArray();
        var details = batteries.SelectMany(item => item.Facts.Where(fact => fact.Label is "Design / full-charge capacity" or "Capacity retention" or "Capacity units")
            .Select(fact => $"{item.Name}: {fact.Label} {fact.Value}")).ToList();
        if (worn.Length > 0)
            return new("battery", title, CheckDomain.HardwareDriverFirmware, CheckStatus.Found, $"{worn.Length} battery pack(s) hold under 80% of design capacity.", details,
                "Capacity is not the same as runtime: a healthy battery can still drain fast under load. Cycle count is not exposed through Windows here.");
        return batteries.All(item => item.Facts.Any(fact => fact.Label == "Capacity retention"))
            ? new("battery", title, CheckDomain.HardwareDriverFirmware, CheckStatus.NotFound, "Battery capacity is at or above 80% of design.", details)
            : new("battery", title, CheckDomain.HardwareDriverFirmware, CheckStatus.CouldNotCheck, "Windows reports battery capacity in relative units, so health cannot be computed.", details);
    }

    // ---- Software evidence ---------------------------------------------------------------------------------------------

    private static TriageCheck AppFaultCheck(TriageInputs inputs, AppFault[] faults)
    {
        const string title = "Application crashes & hangs";
        if (!LogReadable(inputs.Reliability, "Application"))
            return new("app.faults", title, CheckDomain.Software, CheckStatus.CouldNotCheck, "The Application event log could not be read.", []);
        if (faults.Length == 0)
            return new("app.faults", title, CheckDomain.Software, CheckStatus.NotFound, $"No application crashes or hangs recorded in the last {inputs.WindowDays} days.", []);
        var details = new List<string>();
        foreach (var app in faults.GroupBy(fault => fault.App, StringComparer.OrdinalIgnoreCase).OrderByDescending(group => group.Count()).Take(MaxLines))
        {
            var crashes = app.Where(fault => !fault.IsHang).ToArray();
            var hangs = app.Count(fault => fault.IsHang);
            var line = $"{app.Key}: {crashes.Length} crash(es), {hangs} hang(s), latest {Stamp(app.Max(fault => fault.Time))}";
            var top = crashes.GroupBy(fault => (fault.Module, fault.ExceptionCode, fault.Origin)).OrderByDescending(group => group.Count()).FirstOrDefault();
            if (top is not null)
            {
                line += $". Most common: module {top.Key.Module ?? "unknown"}, {ExceptionCodes.Describe(top.Key.ExceptionCode)} ×{top.Count()} ({OriginText(top.Key.Origin)})";
                if (top.Count() >= 3) line += ". Repeats the same fault signature";
            }
            details.Add(line);
        }
        var apps = faults.Select(fault => fault.App.ToLowerInvariant()).Distinct().Count();
        return new("app.faults", title, CheckDomain.Software, CheckStatus.Found,
            $"{faults.Count(fault => !fault.IsHang)} crash(es) and {faults.Count(fault => fault.IsHang)} hang(s) across {apps} application(s).", details,
            "A crash records where the fault surfaced, not why. A crash inside the application's own module, or a repeated identical signature, points at the application; a crash inside a graphics or system module needs a dump to attribute.");
    }

    private static string OriginText(ModuleOrigin origin) => origin switch
    {
        ModuleOrigin.Application => "application's own module",
        ModuleOrigin.WindowsComponent => "Windows or runtime component",
        ModuleOrigin.GraphicsDriver => "graphics driver module",
        _ => "module origin unknown"
    };

    // ---- Context -------------------------------------------------------------------------------------------------------

    private static TriageCheck RestartPending(TriageInputs inputs)
    {
        const string title = "Restart pending";
        if (inputs.System?.RestartIndicators is not { } restart)
            return new("restart", title, CheckDomain.Context, CheckStatus.CouldNotCheck, "Restart signals could not be read.", []);
        return restart.RestartRequested
            ? new("restart", title, CheckDomain.Context, CheckStatus.Found, restart.Status + ".", [], "A pending servicing or update restart can leave drivers and updates half-applied. Restart before testing further.")
            : new("restart", title, CheckDomain.Context, CheckStatus.NotFound, restart.Status + ".", []);
    }

    private static TriageCheck DiskSpace(TriageInputs inputs)
    {
        const string title = "Low disk space";
        if (inputs.System is not { StorageAvailable: true } system)
            return new("space", title, CheckDomain.Context, CheckStatus.CouldNotCheck, "Volume capacity could not be read.", []);
        var low = system.Volumes.Where(volume => volume.FreePercent < 10).ToArray();
        return low.Length == 0
            ? new("space", title, CheckDomain.Context, CheckStatus.NotFound, "All fixed volumes have at least 10% free.", [])
            : new("space", title, CheckDomain.Context, CheckStatus.Found, $"{low.Length} volume(s) under 10% free.",
                low.Select(volume => $"{volume.Name.Trim()}: {volume.FreePercent:F1}% free ({volume.FreeBytes / 1073741824d:F1} GB of {volume.TotalBytes / 1073741824d:F1} GB)").ToArray(),
                "Low free space slows Windows Update, paging and the user's apps without any hardware fault.");
    }

    private static TriageCheck RecentChanges(TriageInputs inputs)
    {
        const string title = "Recent Windows Update changes";
        if (inputs.Updates is null || inputs.Updates.SourceStatus.Any(item => item.StartsWith("Windows Update history: unavailable", StringComparison.OrdinalIgnoreCase)))
            return new("updates", title, CheckDomain.Context, CheckStatus.CouldNotCheck, "Windows Update history could not be read.", []);
        var days = Math.Min(inputs.WindowDays, 14);
        var since = inputs.Now.AddDays(-days);
        // History is read newest first up to a fixed count; if the oldest row read is still inside the window, older changes were not seen.
        var partial = inputs.Updates.TotalHistoryCount is { } total && total > inputs.Updates.Entries.Count
            && inputs.Updates.Entries.Count > 0 && inputs.Updates.Entries.Min(entry => entry.Date) > since;
        var partialNote = partial ? $" Only the newest {inputs.Updates.Entries.Count} history entries were read, which do not reach back {days} days." : string.Empty;
        var recent = inputs.Updates.Entries.Where(entry => entry.Date >= since
            && !entry.Title.Contains("Security Intelligence Update", StringComparison.OrdinalIgnoreCase)).ToArray();
        var failed = recent.Where(entry => entry.Result is "Failed" or "Aborted" or "Succeeded with errors").ToArray();
        var installed = recent.Where(entry => entry.Operation == "Install" && entry.Result.StartsWith("Succeeded", StringComparison.Ordinal)).ToArray();
        if (failed.Length == 0 && installed.Length == 0)
            return new("updates", title, CheckDomain.Context, CheckStatus.NotFound, $"No update installs or failures in the last {days} days (Defender definition updates excluded).", [],
                partial ? partialNote.Trim() : null);
        var lines = failed.GroupBy(entry => entry.Title).Select(group => $"{Stamp(group.Max(entry => entry.Date))} · {group.First().Result}: {group.Key} ({group.First().HResult})")
            .Concat(installed.GroupBy(entry => entry.Title).OrderByDescending(group => group.Max(entry => entry.Date))
                .Select(group => $"{Stamp(group.Max(entry => entry.Date))} · installed: {group.Key}{(group.Count() > 1 ? $" ×{group.Count()}" : string.Empty)}"));
        return new("updates", title, CheckDomain.Context, CheckStatus.Found,
            $"{installed.Select(entry => entry.Title).Distinct().Count()} distinct update(s) installed and {failed.Select(entry => entry.Title).Distinct().Count()} failed in the last {days} days.", Cap(lines),
            "A change shortly before the problem began is a timing correlation, not proof. Driver and firmware updates delivered by Windows Update appear here." + partialNote);
    }

    private static TriageCheck DriverChanges(TriageInputs inputs)
    {
        const string title = "Drivers, BIOS and OS build changed";
        if (inputs.Baseline is null)
            return inputs.BaselineRequested is { } requested
                ? new("changes", title, CheckDomain.Context, CheckStatus.CouldNotCheck, $"The baseline {requested} could not be read, so nothing was compared.", [])
                : new("changes", title, CheckDomain.Context, CheckStatus.NotApplicable, "No earlier snapshot to compare with. This scan becomes the comparison point for the next one.", []);
        if (inputs.Drivers is not { Complete: true })
            return new("changes", title, CheckDomain.Context, CheckStatus.CouldNotCheck, "The driver inventory could not be read completely, so nothing was compared.", []);
        var details = new List<string>();
        if (!string.Equals(inputs.Baseline.BiosVersion, inputs.BiosVersion, StringComparison.OrdinalIgnoreCase) && inputs.Baseline.BiosVersion is not null && inputs.BiosVersion is not null)
            details.Add($"BIOS: {inputs.Baseline.BiosVersion} → {inputs.BiosVersion}");
        var os = inputs.Machine.FirstOrDefault(item => item.Label == "Windows release")?.Value;
        if (inputs.Baseline.OsBuild is not null && os is not null && !string.Equals(inputs.Baseline.OsBuild, os, StringComparison.OrdinalIgnoreCase))
            details.Add($"Windows: {inputs.Baseline.OsBuild} → {os}");
        var changes = DriverComparison.Compare(inputs.Baseline.Drivers, inputs.Drivers.Drivers);
        var vendor = changes.Count(change => !DriverComparison.IsInbox(change.Subject));
        details.AddRange(Cap(changes.Select(change => change.Describe()), 15));
        var compared = $"since {inputs.BaselineLabel ?? Stamp(inputs.Baseline.CreatedAt)}";
        return details.Count == 0
            ? new("changes", title, CheckDomain.Context, CheckStatus.NotFound, $"No driver, BIOS or OS build change {compared}.", [])
            : new("changes", title, CheckDomain.Context, CheckStatus.Found, $"{changes.Count} driver change(s) ({vendor} non-Microsoft){(details.Count > changes.Count || changes.Count == 0 ? " plus firmware/OS changes" : string.Empty)} {compared}.", details,
                "Vendor driver changes are listed before Microsoft inbox drivers. A change is context for the problem's timing, not a diagnosis.");
    }

    private static TriageCheck FirmwareAge(TriageInputs inputs)
    {
        const string title = "BIOS age";
        if (inputs.BiosReleaseDate is not { } released)
            return new("bios", title, CheckDomain.Context, CheckStatus.CouldNotCheck, "BIOS release date could not be read.", []);
        var months = (int)((inputs.Now - released).TotalDays / 30.4);
        var line = string.Create(CultureInfo.InvariantCulture, $"BIOS {inputs.BiosVersion ?? "version unknown"}, released {released:yyyy-MM-dd} ({months} months old).");
        return months >= 24
            ? new("bios", title, CheckDomain.Context, CheckStatus.Found, line, [], "Age alone is not a fault. Compare with the vendor's current release and your fleet baseline for this model.")
            : new("bios", title, CheckDomain.Context, CheckStatus.NotFound, line, []);
    }

    // ---- Helpers -------------------------------------------------------------------------------------------------------

    // WHEA-Logger records fatal errors at Error/Critical level and corrected ones at Warning, independent of the message language.
    // Copies from Reliability Monitor carry no level, so the English message text is the fallback.
    private static bool? WheaFatal(ReliabilityEvent item) => item.Severity switch
    {
        "Critical" or "Error" => true,
        "Warning" => false,
        _ => FatalText().IsMatch(item.Details) ? true : item.Details.Contains("corrected", StringComparison.OrdinalIgnoreCase) ? false : null
    };

    private static bool LogReadable(ReliabilityHistory? history, string log)
        => history is not null && history.SourceStatus.Any(status => status.StartsWith(log + ":", StringComparison.OrdinalIgnoreCase)
            && status.Contains("records scanned", StringComparison.OrdinalIgnoreCase));

    private static List<string> Cap(IEnumerable<string> lines, int max = MaxLines)
    {
        var all = lines.ToList();
        if (all.Count <= max) return all;
        var kept = all.Take(max).ToList();
        kept.Add($"(+{all.Count - max} more)");
        return kept;
    }

    // Some providers (Display, for example) have no installed message text; say what the event is instead of "no formatted message".
    private static string Text(ReliabilityEvent item)
        => item.Summary.StartsWith("No formatted message", StringComparison.OrdinalIgnoreCase) || item.Summary.StartsWith("Message resources unavailable", StringComparison.OrdinalIgnoreCase)
            ? item.Provider.Equals("Display", StringComparison.OrdinalIgnoreCase) && item.EventId == 4101 ? "Display driver stopped responding and recovered"
            : $"{item.Provider} event {item.EventId.ToString(CultureInfo.InvariantCulture)}"
            : item.Summary;

    private static string Stamp(DateTimeOffset time) => time.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

    [GeneratedRegex(@"bugcheck\s+was:\s*(0x[0-9a-fA-F]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BugcheckText();

    [GeneratedRegex(@"\b(uncorrected|fatal)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FatalText();
}
