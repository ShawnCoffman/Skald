using Skald.Core.Models;
using Skald.Triage;

namespace Skald.Recorder;

public sealed record SessionDriver(string Class, string Name, string Provider, string Version, DateTimeOffset? Date, string DeviceId, bool? IsSigned);

// Identity of the recorded machine: make, model, processor, BIOS, Windows build and the installed drivers.
public sealed record SessionMachine(DateTimeOffset CollectedAt, IReadOnlyList<InventoryDetail> Details,
    IReadOnlyList<SessionDriver> Drivers, IReadOnlyList<string> SourceStatus);

// Windows reliability reports and dump files from the recording's own time window, read on the recorded machine.
public sealed record SessionWindowsEvidence(DateTimeOffset CollectedAt, DateTimeOffset From, DateTimeOffset To,
    IReadOnlyList<ReliabilityEvent> Events, IReadOnlyList<CrashDumpFile> Dumps, IReadOnlyList<string> SourceStatus);

// A Windows Performance Recorder trace saved beside the recording. FileName is relative to the recording's folder.
public sealed record SessionTrace(string FileName, DateTimeOffset SavedAt, string Reason, long SizeBytes);

public static class SessionEvidenceCollector
{
    public static async Task<SessionMachine> CollectMachineAsync()
    {
        var system = SystemInventoryCollector.CollectAsync(includeApplications: false);
        var drivers = DriverInventoryCollector.CollectAsync();
        var status = new List<string>();
        var details = new List<InventoryDetail>();
        try
        {
            var inventory = await system.ConfigureAwait(false);
            details.AddRange(inventory.Machine);
            details.AddRange(inventory.OperatingSystem);
            status.AddRange(inventory.Limitations);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { status.Add($"Machine identity: unavailable ({ex.GetType().Name})"); }
        var driverRows = Array.Empty<SessionDriver>();
        try
        {
            var inventory = await drivers.ConfigureAwait(false);
            driverRows = inventory.Drivers.Select(driver => new SessionDriver(driver.Class, driver.Name, driver.Provider, driver.Version, driver.Date, driver.DeviceId, driver.IsSigned))
                .OrderBy(driver => driver.Class, StringComparer.OrdinalIgnoreCase).ThenBy(driver => driver.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            status.AddRange(inventory.SourceStatus);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { status.Add($"Driver inventory: unavailable ({ex.GetType().Name})"); }
        return new(DateTimeOffset.Now, details, driverRows, status);
    }

    public static async Task<SessionWindowsEvidence> CollectWindowsAsync(DateTimeOffset from, DateTimeOffset to)
    {
        var reliability = WindowsReliabilityCollector.CollectAsync(from, to);
        var dumps = CrashDumpCollector.CollectAsync();
        var status = new List<string>();
        ReliabilityEvent[] events = [];
        CrashDumpFile[] files = [];
        try
        {
            var history = await reliability.ConfigureAwait(false);
            events = history.Events.OrderBy(item => item.Timestamp).ToArray();
            status.AddRange(history.SourceStatus);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { status.Add($"Windows reports: unavailable ({ex.GetType().Name})"); }
        try
        {
            var inventory = await dumps.ConfigureAwait(false);
            files = inventory.Files.Where(file => file.ModifiedAt >= from && file.ModifiedAt <= to).OrderBy(file => file.ModifiedAt).ToArray();
            status.AddRange(inventory.SourceStatus);
            status.Add($"Dump files written during the recording: {files.Length}");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { status.Add($"Dump files: unavailable ({ex.GetType().Name})"); }
        return new(DateTimeOffset.Now, from, to, events, files, status);
    }
}
