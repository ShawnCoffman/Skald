using System.Globalization;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Skald.Core.Models;

namespace Skald.Collectors;

internal static class HardwareStorageCollector
{
    private sealed record DiskRecord(uint Number, string? StableIdentity, HardwareDeviceBuilder Device);
    private sealed record CounterRecord(DateTimeOffset At, ulong? ReadErrors, ulong? WriteErrors, ulong? Wear);

    public static void Collect(List<HardwareDeviceBuilder> devices, List<string> status)
    {
        var disks = new Dictionary<uint, DiskRecord>();
        var diskPaths = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        HardwareInventoryCollector.Read(HardwareInventoryCollector.StorageNamespace, "SELECT * FROM MSFT_Disk", "Windows disks", status, row =>
        {
            if (HardwareInventoryCollector.Number(row, "Number") is not { } rawNumber || rawNumber > uint.MaxValue) return;
            var number = (uint)rawNumber;
            var uniqueId = HardwareInventoryCollector.Value(row, "UniqueId");
            var serial = HardwareInventoryCollector.Value(row, "SerialNumber");
            var stable = uniqueId ?? (serial is null ? null : $"{serial}|{HardwareInventoryCollector.Value(row, "Model")}");
            var name = HardwareInventoryCollector.Value(row, "FriendlyName") ?? HardwareInventoryCollector.Value(row, "Model") ?? $"Windows disk {number}";
            var device = new HardwareDeviceBuilder($"disk/{stable ?? number.ToString(CultureInfo.InvariantCulture)}", "Storage",
                $"Disk {number} · {name}", stable ?? $"Windows disk number {number} (can change after restart)",
                stable is null ? HardwareMatchConfidence.Probable : HardwareMatchConfidence.Verified) { WindowsDiskNumber = number };
            device.Fact("Windows disk number", number.ToString(CultureInfo.InvariantCulture), "MSFT_Disk");
            device.Fact("Bus / capacity", $"{BusType(HardwareInventoryCollector.Number(row, "BusType"))} · {HardwareInventoryCollector.Gigabytes(HardwareInventoryCollector.Number(row, "Size"))}", "MSFT_Disk");
            device.Fact("Model", HardwareInventoryCollector.Value(row, "Model"), "MSFT_Disk");
            device.Fact("Manufacturer", HardwareInventoryCollector.Value(row, "Manufacturer"), "MSFT_Disk");
            device.Fact("Serial number", serial, "MSFT_Disk");
            device.Fact("Firmware", HardwareInventoryCollector.Value(row, "FirmwareVersion"), "MSFT_Disk");
            device.Fact("Unique ID", uniqueId, "MSFT_Disk");
            device.Fact("Storage path", HardwareInventoryCollector.Value(row, "Path"), "MSFT_Disk");
            device.Fact("Adapter / bus location", HardwareInventoryCollector.Value(row, "Location"), "MSFT_Disk");
            device.Fact("Logical / physical sector", $"{HardwareInventoryCollector.Value(row, "LogicalSectorSize") ?? "?"} / {HardwareInventoryCollector.Value(row, "PhysicalSectorSize") ?? "?"} bytes", "MSFT_Disk");

            var health = HardwareInventoryCollector.Number(row, "HealthStatus");
            device.Fact("Windows health", health switch { 0 => "Healthy", 1 => "Warning", 2 => "Unhealthy", _ => "Unavailable" }, "MSFT_Disk");
            if (health == 0) device.Signal("Windows reports healthy", "This is the storage provider's status, not a full diagnostic test.", HardwareHealthState.NoProblemObserved, "MSFT_Disk");
            else if (health == 1) device.Signal("Windows storage warning", "The storage provider reports a health warning.", HardwareHealthState.Attention, "MSFT_Disk");
            else if (health == 2) device.Signal("Windows reports unhealthy", "The storage provider reports the disk unhealthy.", HardwareHealthState.ReportedProblem, "MSFT_Disk");
            var operational = OperationalCodes(row);
            device.Fact("Operational status", operational.Length == 0 ? "Unavailable" : string.Join(", ", operational.Select(OperationalStatus)), "MSFT_Disk");
            if (operational.Any(code => code is 3 or 4 or 5 or 6 or 7 or 12 or 13 or 16 or 0xD011 or 0xD014))
                device.Signal("Storage operational state", string.Join(", ", operational.Select(OperationalStatus)), HardwareHealthState.Attention, "MSFT_Disk");
            if (HardwareInventoryCollector.Value(row, "IsOffline")?.Equals("True", StringComparison.OrdinalIgnoreCase) == true)
                device.Signal("Disk offline", "Windows reports this disk offline. It may be intentionally offline.", HardwareHealthState.Attention, "MSFT_Disk");
            disks[number] = new(number, stable, device);
            if (row is ManagementObject managementDisk && ReferenceKey(managementDisk.Path.Path) is { } diskPath)
                diskPaths[diskPath] = number;
            devices.Add(device);
        });

        HardwareInventoryCollector.Read(HardwareInventoryCollector.StorageNamespace, "SELECT DiskNumber,AccessPaths,DriveLetter,IsBoot,IsSystem FROM MSFT_Partition", "Disk partitions", status, row =>
        {
            if (HardwareInventoryCollector.Number(row, "DiskNumber") is not { } raw || raw > uint.MaxValue || !disks.TryGetValue((uint)raw, out var disk)) return;
            var paths = row.Properties["AccessPaths"]?.Value as string[];
            var pathText = paths is { Length: > 0 } ? string.Join(", ", paths.Where(path => !string.IsNullOrWhiteSpace(path)).Take(5)) : null;
            pathText ??= HardwareInventoryCollector.Value(row, "DriveLetter") is { } letter ? letter + @":\" : null;
            if (pathText is not null) disk.Device.Fact("Partition access path", pathText, "MSFT_Partition");
            if (HardwareInventoryCollector.Value(row, "IsBoot")?.Equals("True", StringComparison.OrdinalIgnoreCase) == true)
                disk.Device.Fact("Windows role", "Contains current boot partition", "MSFT_Partition");
            if (HardwareInventoryCollector.Value(row, "IsSystem")?.Equals("True", StringComparison.OrdinalIgnoreCase) == true)
                disk.Device.Fact("Windows role", "Contains system partition", "MSFT_Partition");
        });

        HardwareInventoryCollector.Read(HardwareInventoryCollector.CimNamespace, "SELECT Index,PNPDeviceID FROM Win32_DiskDrive", "Disk PnP identities", status, row =>
        {
            if (HardwareInventoryCollector.Number(row, "Index") is { } raw && raw <= uint.MaxValue && disks.TryGetValue((uint)raw, out var disk))
                disk.Device.PnpId = HardwareInventoryCollector.Value(row, "PNPDeviceID");
        });

        var oldCounters = LoadCounters(status);
        var newCounters = new Dictionary<string, CounterRecord>(StringComparer.OrdinalIgnoreCase);
        var counterAssociations = new Dictionary<string, HashSet<uint>>(StringComparer.OrdinalIgnoreCase);
        HardwareInventoryCollector.Read(HardwareInventoryCollector.StorageNamespace,
            "SELECT Disk,StorageReliabilityCounter FROM MSFT_DiskToStorageReliabilityCounter",
            "Disk-to-reliability associations", status, row =>
            {
                var diskReference = HardwareInventoryCollector.Value(row, "Disk");
                var counterReference = HardwareInventoryCollector.Value(row, "StorageReliabilityCounter");
                var diskPath = ReferenceKey(diskReference);
                var key = ReferenceKey(counterReference);
                if (diskPath is null || key is null || !diskPaths.TryGetValue(diskPath, out var associatedNumber)) return;
                if (!counterAssociations.TryGetValue(key, out var numbers)) counterAssociations[key] = numbers = [];
                numbers.Add(associatedNumber);
            });
        HardwareInventoryCollector.Read(HardwareInventoryCollector.StorageNamespace, "SELECT * FROM MSFT_StorageReliabilityCounter", "Storage reliability counters", status, row =>
        {
            var id = HardwareInventoryCollector.Value(row, "DeviceId");
            if (id is null) return;
            var counterPathKey = row is ManagementObject counter ? ReferenceKey(counter.Path.Path) : null;
            var associated = counterPathKey is not null && counterAssociations.TryGetValue(counterPathKey, out var numbers) ? numbers : null;
            if (associated is not { Count: 1 } || !disks.TryGetValue(associated.Single(), out var disk))
            {
                var orphan = new HardwareDeviceBuilder($"storage-counter/{id}", "Storage", $"Unmapped storage counter {id}",
                    $"Provider device ID {id}", HardwareMatchConfidence.Unmapped);
                orphan.Fact("Mapping", associated is { Count: > 1 } ? "Counter is associated with multiple Windows disks" : "No explicit disk association was exposed; health values are not assigned to a drive", "MSFT_DiskToStorageReliabilityCounter", HardwareMatchConfidence.Unmapped);
                orphan.Fact("Temperature", HardwareInventoryCollector.Value(row, "Temperature") is { } orphanTemperature ? orphanTemperature + " °C" : "Unavailable", "MSFT_StorageReliabilityCounter", HardwareMatchConfidence.Unmapped);
                orphan.Fact("Wear used", HardwareInventoryCollector.Value(row, "Wear") is { } orphanWear ? orphanWear + "%" : "Unavailable", "MSFT_StorageReliabilityCounter", HardwareMatchConfidence.Unmapped);
                orphan.Fact("Uncorrected read / write errors", $"{HardwareInventoryCollector.Value(row, "ReadErrorsUncorrected") ?? "Unavailable"} / {HardwareInventoryCollector.Value(row, "WriteErrorsUncorrected") ?? "Unavailable"}", "MSFT_StorageReliabilityCounter", HardwareMatchConfidence.Unmapped);
                devices.Add(orphan);
                return;
            }
            var device = disk.Device;
            device.Fact("Reliability-counter match", $"Explicit association to Windows disk {disk.Number} (provider ID {id})", "MSFT_DiskToStorageReliabilityCounter");
            var temperature = HardwareInventoryCollector.Number(row, "Temperature");
            var wear = HardwareInventoryCollector.Number(row, "Wear");
            var reads = HardwareInventoryCollector.Number(row, "ReadErrorsUncorrected");
            var writes = HardwareInventoryCollector.Number(row, "WriteErrorsUncorrected");
            device.Fact("Temperature", temperature is > 0 ? $"{temperature} °C" : "Unavailable", "MSFT_StorageReliabilityCounter");
            device.Fact("Wear used", wear is { } w ? $"{w}%" : "Unavailable", "MSFT_StorageReliabilityCounter");
            device.Fact("Uncorrected read / write errors", $"{reads?.ToString(CultureInfo.InvariantCulture) ?? "Unavailable"} / {writes?.ToString(CultureInfo.InvariantCulture) ?? "Unavailable"}", "MSFT_StorageReliabilityCounter");
            device.Fact("Power-on hours", HardwareInventoryCollector.Value(row, "PowerOnHours"), "MSFT_StorageReliabilityCounter");
            device.Fact("Maximum reported read / write latency", $"{HardwareInventoryCollector.Value(row, "ReadLatencyMax") ?? "?"} / {HardwareInventoryCollector.Value(row, "WriteLatencyMax") ?? "?"} ms", "MSFT_StorageReliabilityCounter");
            if (wear is >= 100) device.Signal("Estimated wear limit reached", $"Provider reports {wear}% wear used.", HardwareHealthState.Attention, "MSFT_StorageReliabilityCounter");
            if (reads is > 0 || writes is > 0) device.Signal("Uncorrected errors reported", $"Historical counts: {reads ?? 0} read, {writes ?? 0} write. Counts do not establish when errors occurred.", HardwareHealthState.Attention, "MSFT_StorageReliabilityCounter");

            if (disk.StableIdentity is not { } stable) return;
            var key = StableHash(stable);
            var now = DateTimeOffset.UtcNow;
            if (oldCounters.TryGetValue(key, out var old))
            {
                AddIncrease(device, "uncorrected read errors", old.ReadErrors, reads, old.At, now);
                AddIncrease(device, "uncorrected write errors", old.WriteErrors, writes, old.At, now);
            }
            newCounters[key] = new(now, reads, writes, wear);
        });
        SaveCounters(newCounters, status);
    }

    private static void AddIncrease(HardwareDeviceBuilder device, string label, ulong? before, ulong? after,
        DateTimeOffset previousAt, DateTimeOffset currentAt)
    {
        if (before is { } old && after is { } current && current > old)
            device.Signal($"{label} increased", $"{old} → {current} since {previousAt.ToLocalTime():g}; change observed by {currentAt.ToLocalTime():g}.",
                HardwareHealthState.Attention, "Storage reliability counter", currentAt);
    }

    private static Dictionary<string, CounterRecord> LoadCounters(List<string> status)
    {
        try
        {
            var path = CounterPath();
            return File.Exists(path)
                ? JsonSerializer.Deserialize<Dictionary<string, CounterRecord>>(File.ReadAllText(path)) ?? [] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            status.Add($"Stored health history: unavailable ({ex.GetType().Name})");
            return [];
        }
    }

    private static void SaveCounters(Dictionary<string, CounterRecord> counters, List<string> status)
    {
        if (counters.Count == 0) return;
        try
        {
            var path = CounterPath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(counters));
            File.Move(temporary, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            status.Add($"Stored health history: could not save ({ex.GetType().Name})");
        }
    }

    private static string CounterPath()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(string.IsNullOrWhiteSpace(root) ? AppContext.BaseDirectory : root, "Skald", "storage-health.json");
    }

    private static string StableHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string? ReferenceKey(string? reference)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        try { return new ManagementPath(reference).RelativePath; }
        catch (Exception ex) when (ex is ManagementException or ArgumentException) { return null; }
    }
    private static ulong[] OperationalCodes(ManagementBaseObject row)
    {
        try
        {
            return row.Properties["OperationalStatus"]?.Value is Array values
                ? values.Cast<object>().Select(item => Convert.ToUInt64(item, CultureInfo.InvariantCulture)).ToArray()
                : [];
        }
        catch (Exception ex) when (ex is ManagementException or InvalidCastException or FormatException or OverflowException)
        {
            return [];
        }
    }
    private static string BusType(ulong? value) => value switch
    {
        3 => "ATA", 7 => "USB", 8 => "RAID", 10 => "SAS", 11 => "SATA", 14 => "Virtual",
        15 => "File-backed virtual", 16 => "Storage Spaces", 17 => "NVMe", null => "Unknown", _ => $"Bus {value}"
    };
    private static string OperationalStatus(ulong value) => value switch
    {
        2 => "OK", 3 => "Degraded", 4 => "Stressed", 5 => "Predictive failure", 6 => "Error",
        7 => "Non-recoverable error", 12 => "No contact", 13 => "Lost communication",
        16 => "Supporting entity in error", 0xD011 => "Not ready", 0xD014 => "Failed",
        _ => $"Code {value}"
    };
}
