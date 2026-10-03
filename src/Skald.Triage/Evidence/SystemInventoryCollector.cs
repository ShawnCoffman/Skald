using System.Globalization;
using System.Management;
using Microsoft.Win32;
using Skald.Core.Models;
using Windows.Management.Deployment;

namespace Skald.Triage;

public static class SystemInventoryCollector
{
    public static Task<SystemInventory> CollectAsync(bool includeApplications = true) => Task.Run(() => Collect(includeApplications));

    private static SystemInventory Collect(bool includeApplications)
    {
        var result = new SystemInventory();
        result.Machine.Add(new("System name", Environment.MachineName));
        Read("Machine identity", "Win32_ComputerSystem", "Manufacturer,Model,TotalPhysicalMemory", row =>
        {
            result.Machine.Add(new("Make / model", $"{Value(row, "Manufacturer")} · {Value(row, "Model")}"));
            result.Machine.Add(new("Usable memory", ulong.TryParse(Value(row, "TotalPhysicalMemory"), out var bytes) ? $"{bytes / 1073741824d:F1} GB" : "Unknown"));
        }, result);
        ulong installedBytes = 0;
        var memoryAvailable = Read("Installed memory", "Win32_PhysicalMemory", "Capacity", row => { if (row["Capacity"] is ulong capacity) installedBytes += capacity; }, result);
        result.Machine.Add(new("Installed memory", memoryAvailable && installedBytes > 0 ? $"{installedBytes / 1073741824d:F1} GB" : "Unknown"));
        Read("Processor", "Win32_Processor", "Name,NumberOfCores,NumberOfLogicalProcessors", row =>
            result.Machine.Add(new("Processor", $"{Value(row, "Name")} · {Value(row, "NumberOfCores")} cores / {Value(row, "NumberOfLogicalProcessors")} logical")), result);
        Read("Graphics", "Win32_VideoController", "Name", row => result.Machine.Add(new("Graphics", Value(row, "Name"))), result);
        Read("Operating system", "Win32_OperatingSystem", "Caption,Version,BuildNumber,OSArchitecture,LastBootUpTime", row =>
        {
            result.OperatingSystem.Add(new("Operating system", $"{Value(row, "Caption")} · {Value(row, "OSArchitecture")}"));
            result.OperatingSystem.Add(new("Version / build", $"{Value(row, "Version")} (build {Value(row, "BuildNumber")})"));
            result.OperatingSystem.Add(new("Last boot", DateValue(row, "LastBootUpTime")));
        }, result);
        Read("BIOS", "Win32_BIOS", "SMBIOSBIOSVersion,Manufacturer,ReleaseDate", row =>
        {
            result.OperatingSystem.Add(new("BIOS version", $"{Value(row, "Manufacturer")} · {Value(row, "SMBIOSBIOSVersion")}"));
            var date = Value(row, "ReleaseDate");
            result.OperatingSystem.Add(new("BIOS date", date.Length >= 8 && DateTime.TryParseExact(date[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var release) ? release.ToString("d", CultureInfo.CurrentCulture) : "Unknown"));
        }, result);
        try
        {
            using var version = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            result.OperatingSystem.Add(new("Windows release", $"{version?.GetValue("DisplayVersion") ?? "Unknown"} · build {version?.GetValue("CurrentBuildNumber") ?? "Unknown"}.{version?.GetValue("UBR") ?? "?"}"));
            using var secureBoot = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\SecureBoot\State");
            result.OperatingSystem.Add(new("Secure Boot", secureBoot?.GetValue("UEFISecureBootEnabled") is int enabled ? enabled == 1 ? "Enabled" : "Disabled" : "Unknown"));
        }
        catch (Exception ex) when (IsInventoryException(ex)) { result.Limitations.Add("Windows release / Secure Boot: Unknown"); }
        result.DevicesAvailable = Read("Device status", "Win32_PnPEntity", "Name,PNPDeviceID,ConfigManagerErrorCode,Present", row =>
        {
            if (row["Present"] is false) return;
            if (row["ConfigManagerErrorCode"] is not uint code) { result.DevicesAvailable = false; return; }
            if (code != 0) result.DeviceProblems.Add(new(Value(row, "Name"), code, Value(row, "PNPDeviceID")));
        }, result) && result.DevicesAvailable;
        result.StorageAvailable = Read("Storage", "Win32_LogicalDisk", "DeviceID,VolumeName,FreeSpace,Size", row =>
        {
            if (row["FreeSpace"] is ulong free && row["Size"] is ulong total && total > 0)
                result.Volumes.Add(new($"{Value(row, "DeviceID")} {Value(row, "VolumeName")}", free, total));
        }, result, " WHERE DriveType = 3");
        if (includeApplications) ReadApplications(result);
        try
        {
            using var cbs = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending");
            using var update = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired");
            using var session = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager");
            var fileOperations = session?.GetValue("PendingFileRenameOperations") as string[];
            var queued = fileOperations?.Where((path, index) => index % 2 == 0 && !string.IsNullOrWhiteSpace(path)).Count() ?? 0;
            result.RestartIndicators = new(cbs is not null, update is not null, queued);
            result.OperatingSystem.Add(new("Restart indicators", result.RestartIndicators.Status));
        }
        catch (Exception ex) when (IsInventoryException(ex)) { result.Limitations.Add("Restart signals: Unknown"); }
        return result;
    }

    private static void ReadApplications(SystemInventory result)
    {
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var key in uninstall.GetSubKeyNames())
                {
                    using var app = uninstall.OpenSubKey(key);
                    if (app?.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name) || app.GetValue("SystemComponent") is 1 || app.GetValue("ParentKeyName") is not null) continue;
                    result.Applications.Add(new(name.Trim(), app.GetValue("DisplayVersion")?.ToString() ?? "", app.GetValue("Publisher")?.ToString() ?? "", hive == RegistryHive.LocalMachine ? "Machine · desktop" : "Current user · desktop"));
                }
            }
            catch (Exception ex) when (IsInventoryException(ex)) { result.ApplicationsAvailable = false; result.Limitations.Add($"Desktop apps ({hive}, {view}): partial / unavailable"); }
        }
        try
        {
            var manager = new PackageManager();
            foreach (var package in manager.FindPackagesForUser(string.Empty))
            {
                if (package.IsFramework || package.IsResourcePackage) continue;
                var version = package.Id.Version;
                result.Applications.Add(new(package.DisplayName is { Length: > 0 } name ? name : package.Id.Name,
                    $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}", package.PublisherDisplayName, "Current user · packaged"));
            }
        }
        catch (Exception ex) when (IsInventoryException(ex)) { result.ApplicationsAvailable = false; result.Limitations.Add("Packaged apps: partial / unavailable"); }
        var distinct = result.Applications.DistinctBy(app => $"{app.Name}|{app.Version}|{app.Publisher}|{app.Scope}", StringComparer.OrdinalIgnoreCase).OrderBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        result.Applications.Clear();
        result.Applications.AddRange(distinct);
    }

    private static bool Read(string label, string type, string properties, Action<ManagementBaseObject> consume, SystemInventory result, string filter = "")
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("root\\CIMV2", $"SELECT {properties} FROM {type}{filter}", new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(8) });
            using var rows = searcher.Get();
            var found = false;
            foreach (ManagementBaseObject row in rows) { using (row) consume(row); found = true; }
            if (!found) AddUnavailable(label, result, "no records returned");
            return found;
        }
        catch (Exception ex) when (IsInventoryException(ex)) { AddUnavailable(label, result, ex.GetType().Name); return false; }
    }

    private static void AddUnavailable(string label, SystemInventory result, string reason)
    {
        result.Limitations.Add($"{label}: Unknown ({reason})");
        if (label is "Machine identity" or "Processor" or "Graphics") result.Machine.Add(new(label, "Unknown"));
        if (label is "Operating system" or "BIOS") result.OperatingSystem.Add(new(label, "Unknown"));
    }

    private static string Value(ManagementBaseObject row, string property) => row[property]?.ToString()?.Trim() is { Length: > 0 } value ? value : "Unknown";
    private static string DateValue(ManagementBaseObject row, string property)
    {
        try { return ManagementDateTimeConverter.ToDateTime(Value(row, property)).ToString("g", CultureInfo.CurrentCulture); }
        catch (ArgumentOutOfRangeException) { return "Unknown"; }
    }
    private static bool IsInventoryException(Exception ex) => ex is ManagementException or UnauthorizedAccessException or System.Security.SecurityException or System.Runtime.InteropServices.COMException or IOException or ArgumentException or InvalidOperationException;
}

