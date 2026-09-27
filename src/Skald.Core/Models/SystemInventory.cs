namespace Skald.Core.Models;

public sealed record InventoryDetail(string Label, string Value);
public sealed record InstalledApplication(string Name, string Version, string Publisher, string Scope);
public sealed record DeviceProblem(string Name, uint Code, string DeviceId)
{
    public bool IsDisabled => Code == 22;
    public string Status => Code switch
    {
        1 => "Not configured correctly", 10 => "Cannot start", 12 => "Insufficient resources",
        14 => "Restart required", 18 => "Reinstall drivers", 19 => "Registry configuration problem",
        21 => "Being removed", 22 => "Disabled", 24 => "Not present or not working",
        28 => "Drivers not installed", 31 => "Windows cannot load required drivers",
        32 => "Driver service disabled", 43 => "Device reported a problem",
        45 => "Not connected", 48 => "Driver blocked", 52 => "Driver signature could not be verified",
        _ => "Device Manager problem"
    };
    public string Description => $"{Name} · Code {Code} · {Status}";
}
public sealed record StorageVolume(string Name, ulong FreeBytes, ulong TotalBytes)
{
    public double FreePercent => TotalBytes == 0 ? 0 : 100d * FreeBytes / TotalBytes;
}
public sealed record RestartIndicators(bool Servicing, bool WindowsUpdate, int QueuedFileOperations)
{
    public bool RestartRequested => Servicing || WindowsUpdate;
    public string Status => RestartRequested
        ? $"Restart requested by {(Servicing && WindowsUpdate ? "Windows servicing and Windows Update" : Servicing ? "Windows servicing" : "Windows Update")}" 
        : QueuedFileOperations > 0
            ? $"No servicing or Windows Update reboot request; {QueuedFileOperations} file operation{(QueuedFileOperations == 1 ? "" : "s")} queued for next boot"
            : "No known restart signal";
}
public sealed class SystemInventory
{
    public DateTimeOffset CollectedAt { get; init; } = DateTimeOffset.Now;
    public List<InventoryDetail> Machine { get; } = [];
    public List<InventoryDetail> OperatingSystem { get; } = [];
    public List<InstalledApplication> Applications { get; } = [];
    public List<DeviceProblem> DeviceProblems { get; } = [];
    public List<StorageVolume> Volumes { get; } = [];
    public List<string> Limitations { get; } = [];
    public bool ApplicationsAvailable { get; set; } = true;
    public bool DevicesAvailable { get; set; } = true;
    public bool StorageAvailable { get; set; } = true;
    public RestartIndicators? RestartIndicators { get; set; }
}
