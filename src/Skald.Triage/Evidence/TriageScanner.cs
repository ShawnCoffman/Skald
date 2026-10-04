using System.Globalization;
using System.Security.Principal;
using Skald.Core.Models;

namespace Skald.Triage;

public sealed record TriageOptions
{
    public int WindowDays { get; init; } = 30;
    // Compare against this snapshot instead of the previous scan's, and leave the previous-scan snapshot untouched.
    public string? BaselinePath { get; init; }
    public string? SaveBaselinePath { get; init; }
    public bool UpdateLastScanSnapshot { get; init; } = true;
}

public sealed record TriageResult(TriageReport Report, DriverInventory? Drivers, DriverSnapshot? Snapshot);

public static class TriageInfo
{
    public const string Version = "0.3.0";
}

public static class TriageScanner
{
    public static async Task<TriageResult> RunAsync(TriageOptions options, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.WindowDays, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.WindowDays, 30);
        var status = new List<string>();
        var now = DateTimeOffset.Now;

        progress?.Report("Reading event logs and system inventory…");
        var reliabilityTask = Guard(WindowsReliabilityCollector.CollectAsync(), "Event logs", status);
        var systemTask = Guard(SystemInventoryCollector.CollectAsync(includeApplications: false), "System inventory", status);
        var driversTask = Guard(DriverInventoryCollector.CollectAsync(), "Driver inventory", status);
        var dumpsTask = Guard(CrashDumpCollector.CollectAsync(), "Crash dumps", status);
        var updatesTask = Guard(WindowsUpdateCollector.CollectAsync(since: now.AddDays(-options.WindowDays)), "Windows Update", status);
        var devicesTask = Guard(DeviceEvidenceCollector.CollectAsync(since: now.AddDays(-options.WindowDays)), "Device evidence", status);
        var settingsTask = Guard(DeviceSettingsCollector.CollectAsync(), "Device settings", status);

        // Collectors cannot be interrupted, but the caller can stop waiting for them.
        var reliability = await reliabilityTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report("Reading hardware inventory (disks, memory, battery, firmware)…");
        var hardware = await Guard(HardwareInventoryCollector.CollectAsync(reliability), "Hardware inventory", status).WaitAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report("Reading drivers, dumps and update history…");
        var system = await systemTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        var drivers = await driversTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        var dumps = await dumpsTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        var updates = await updatesTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        progress?.Report("Reading devices, device drivers and device settings…");
        var devices = await devicesTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        var settings = await settingsTask.WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        progress?.Report("Reading crash dump headers…");
        var headers = await Task.Run(() => ReadDumpHeaders(dumps, now.AddDays(-options.WindowDays), status), cancellationToken).ConfigureAwait(false);

        foreach (var (prefix, lines) in new (string, IEnumerable<string>?)[]
                 {
                     ("Events", reliability?.SourceStatus), ("Hardware", hardware?.SourceStatus.Where(line => !line.StartsWith("Events · ", StringComparison.Ordinal))), ("Drivers", drivers?.SourceStatus),
                     ("Dumps", dumps?.SourceStatus), ("Updates", updates?.SourceStatus), ("System", system?.Limitations),
                     ("Devices", devices?.SourceStatus), ("Settings", settings?.SourceStatus)
                 })
            if (lines is not null) status.AddRange(lines.Select(line => $"{prefix} · {line}"));

        progress?.Report("Evaluating…");
        var machine = MachineDetails(system);
        var firmware = hardware?.Devices.FirstOrDefault(device => device.Key == "platform/firmware");
        var biosVersion = firmware?.Facts.FirstOrDefault(fact => fact.Label == "BIOS version")?.Value;
        var biosDate = DriverComparison.ParseCimDate(firmware?.Facts.FirstOrDefault(fact => fact.Label == "Release date")?.Value);
        var os = system?.OperatingSystem.FirstOrDefault(item => item.Label == "Windows release")?.Value;
        var model = system?.Machine.FirstOrDefault(item => item.Label == "Make / model")?.Value;

        var baselinePath = options.BaselinePath ?? DriverSnapshotStore.LastScanPath;
        var baseline = DriverSnapshotStore.Load(baselinePath, status);
        if (options.BaselinePath is not null && baseline is null) status.Add($"Baseline {Path.GetFileName(options.BaselinePath)}: could not be read; nothing was compared.");
        var label = options.BaselinePath is not null ? Path.GetFileName(options.BaselinePath)
            : baseline is null ? null : string.Create(CultureInfo.InvariantCulture, $"the previous scan ({baseline.CreatedAt.ToLocalTime():yyyy-MM-dd HH:mm})");

        var inputs = new TriageInputs(now, options.WindowDays, IsElevated())
        {
            Reliability = reliability, Hardware = hardware, System = system, Dumps = dumps, Updates = updates, Drivers = drivers, Devices = devices, Settings = settings,
            Baseline = baseline, BaselineLabel = label, BaselineRequested = options.BaselinePath is null ? null : Path.GetFileName(options.BaselinePath), DumpHeaders = headers, BiosVersion = biosVersion, BiosReleaseDate = biosDate,
            Machine = machine, SourceStatus = status
        };
        var report = TriageEngine.Evaluate(inputs, TriageInfo.Version);

        // A partial driver list would show hundreds of false "removed" drivers next time, so it never becomes a snapshot.
        DriverSnapshot? snapshot = drivers is { Complete: true } ? new(now, Environment.MachineName, model, biosVersion, os, drivers.Drivers) : null;
        return new(report with { SourceStatus = status.ToArray() }, drivers, snapshot);
    }

    // Saves the snapshots once the caller has the result safely (shown, or written to disk), so a failed export does not
    // move the comparison point and hide real driver changes from the next scan. Returns any save problems.
    public static IReadOnlyList<string> CommitSnapshots(TriageResult result, TriageOptions options)
    {
        var problems = new List<string>();
        if (result.Snapshot is not { } snapshot) return problems;
        if (options.SaveBaselinePath is not null) DriverSnapshotStore.Save(options.SaveBaselinePath, snapshot with { Label = "baseline" }, problems);
        if (options.UpdateLastScanSnapshot && options.BaselinePath is null) DriverSnapshotStore.Save(DriverSnapshotStore.LastScanPath, snapshot, problems);
        return problems;
    }

    public static bool IsElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or InvalidOperationException) { return false; }
    }

    private static List<InventoryDetail> MachineDetails(SystemInventory? system)
    {
        var details = new List<InventoryDetail>();
        if (system is null) { details.Add(new("System name", Environment.MachineName)); return details; }
        details.AddRange(system.Machine);
        details.AddRange(system.OperatingSystem);
        var serial = new List<string>();
        HardwareInventoryCollector.Read(HardwareInventoryCollector.CimNamespace, "SELECT SerialNumber FROM Win32_BIOS", "Serial number", serial,
            row => { if (HardwareInventoryCollector.Value(row, "SerialNumber") is { } value) details.Add(new("Serial number", value)); });
        return details;
    }

    private static Dictionary<string, DumpHeaderInfo> ReadDumpHeaders(CrashDumpInventory? dumps, DateTimeOffset since, List<string> status)
    {
        var headers = new Dictionary<string, DumpHeaderInfo>(StringComparer.OrdinalIgnoreCase);
        if (dumps is null) return headers;
        // Only the first 96 bytes of each file are read, so every in-window dump is checked (the cap only guards a runaway folder).
        var candidates = dumps.Files.Where(file => file.ModifiedAt >= since && file.Kind.StartsWith("System", StringComparison.Ordinal)).Take(500).ToArray();
        foreach (var file in candidates)
            if (DumpHeaderReader.TryRead(file.Path) is { } header) headers[file.Path] = header;
        if (candidates.Length > 0) lock (status) status.Add($"Dump headers: {headers.Count} of {candidates.Length} system dump(s) readable");
        return headers;
    }

    private static async Task<T?> Guard<T>(Task<T> task, string label, List<string> status) where T : class
    {
        try { return await task.ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lock (status) status.Add($"{label}: failed ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }
}
