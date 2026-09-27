using System.Globalization;
using System.Management;
using System.Net.NetworkInformation;
using Skald.Core.Models;

namespace Skald.Collectors;

public static class HardwareInventoryCollector
{
    internal const string StorageNamespace = @"root\Microsoft\Windows\Storage";
    internal const string CimNamespace = @"root\CIMV2";
    internal const string BatteryNamespace = @"root\wmi";

    public static Task<HardwareInventory> CollectAsync() => Task.Run(Collect);

    private static HardwareInventory Collect()
    {
        var status = new List<string>();
        var devices = new List<HardwareDeviceBuilder>();
        HardwareStorageCollector.Collect(devices, status);
        CollectMemory(devices, status);
        CollectProcessors(devices, status);
        CollectGraphics(devices, status);
        CollectNetwork(devices, status);
        CollectBatteries(devices, status);
        CollectPlatform(devices, status);
        AttachPnpAndDrivers(devices, status);

        var unmapped = new List<HardwareSignal>();
        try
        {
            var reliability = WindowsReliabilityCollector.CollectAsync().GetAwaiter().GetResult();
            var currentBoot = DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64) - TimeSpan.FromMinutes(5);
            var disksByNumber = devices.Where(device => device.WindowsDiskNumber.HasValue)
                .ToLookup(device => device.WindowsDiskNumber!.Value);
            var linkedPerDisk = new Dictionary<uint, int>();
            var unmappedStorage = 0;
            var omittedStorage = 0;
            foreach (var item in reliability.Events)
            {
                var link = StorageIncidentAnalysis.Link(item);
                if (link is null) continue;
                var targets = link.DiskNumber is { } number ? disksByNumber[number].ToArray() : [];
                if (item.Timestamp >= currentBoot && targets.Length == 1)
                {
                    var count = linkedPerDisk.GetValueOrDefault(link.DiskNumber!.Value);
                    if (count >= 50) { omittedStorage++; continue; }
                    linkedPerDisk[link.DiskNumber.Value] = count + 1;
                    targets[0].Signal(link.Description, $"{item.Summary} Windows disk number is a probable match within this boot; the event does not prove the drive itself failed.",
                        HardwareHealthState.Attention, $"{item.Log} / {item.Provider} · event {item.EventId}", item.Timestamp);
                }
                else
                {
                    if (unmappedStorage++ >= 50) { omittedStorage++; continue; }
                    var reason = item.Timestamp < currentBoot ? "Event predates this boot, so its disk number is not assigned to today's disk." :
                        targets.Length > 1 ? "Multiple current disks matched the number." : "No unique current disk was identified.";
                    unmapped.Add(new HardwareSignal(link.Description, $"{item.Summary} {reason}", HardwareHealthState.Attention,
                        $"{item.Log} / {item.Provider} · event {item.EventId}", item.Timestamp));
                }
            }
            status.Add($"Storage events: {linkedPerDisk.Values.Sum()} linked by current-boot disk number, {Math.Min(unmappedStorage, 50)} unmapped" +
                (omittedStorage > 0 ? $", {omittedStorage} older reports omitted from Hardware (see Reliability & Events)" : string.Empty));
            foreach (var item in reliability.Events.Where(item => item.Category == "Hardware").Take(30))
            {
                // WHEA's reported error source is not automatically the failed replaceable part.
                unmapped.Add(new HardwareSignal($"WHEA · event {item.EventId}", item.Summary,
                    HardwareHealthState.Attention, $"{item.Log} / {item.Provider}", item.Timestamp));
            }
            status.AddRange(reliability.SourceStatus.Select(item => "Events · " + item));
        }
        catch (Exception ex) when (IsSourceException(ex))
        {
            status.Add($"Hardware event history: unavailable ({ex.GetType().Name})");
        }

        return new HardwareInventory(DateTimeOffset.Now,
            devices.Select(item => item.Build()).OrderBy(item => CategoryOrder(item.Category))
                .ThenBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToArray(),
            unmapped, status);
    }

    private static int CategoryOrder(string category) => category switch
    {
        "Storage" => 0, "Graphics" => 1, "Processor" => 2, "Memory" => 3,
        "Network" => 4, "Battery" => 5, "Platform" => 6, _ => 7
    };

    private static void CollectMemory(List<HardwareDeviceBuilder> devices, List<string> status)
    {
        var index = 0;
        Read(CimNamespace, "SELECT * FROM Win32_PhysicalMemory", "Memory modules", status, row =>
        {
            index++;
            var location = Value(row, "DeviceLocator") ?? Value(row, "BankLabel") ?? $"Slot {index}";
            var device = new HardwareDeviceBuilder($"memory/{location}", "Memory", location,
                "SMBIOS memory-device location", HardwareMatchConfidence.Verified);
            device.Fact("Capacity", Gigabytes(Number(row, "Capacity")), "Win32_PhysicalMemory");
            device.Fact("Manufacturer", Value(row, "Manufacturer"), "Win32_PhysicalMemory");
            device.Fact("Part number", Value(row, "PartNumber"), "Win32_PhysicalMemory");
            device.Fact("Rated / configured speed", $"{Value(row, "Speed") ?? "Unknown"} / {Value(row, "ConfiguredClockSpeed") ?? "Unknown"} MT/s", "Win32_PhysicalMemory");
            device.Fact("Bank", Value(row, "BankLabel"), "Win32_PhysicalMemory");
            device.Fact("Width", $"Data {Value(row, "DataWidth") ?? "?"} / total {Value(row, "TotalWidth") ?? "?"} bits", "Win32_PhysicalMemory");
            device.Signal("Module health not directly measured", "No per-module fault conclusion is possible without a location-specific hardware error record.", HardwareHealthState.Unknown, "Windows inventory");
            devices.Add(device);
        });
    }

    private static void CollectProcessors(List<HardwareDeviceBuilder> devices, List<string> status)
    {
        var index = 0;
        Read(CimNamespace, "SELECT * FROM Win32_Processor", "Processors", status, row =>
        {
            index++;
            var name = Value(row, "Name") ?? $"Processor {index}";
            var device = new HardwareDeviceBuilder($"cpu/{index}", "Processor", name,
                $"Windows processor socket {index}", HardwareMatchConfidence.Verified);
            device.Fact("Cores / logical processors", $"{Value(row, "NumberOfCores") ?? "?"} / {Value(row, "NumberOfLogicalProcessors") ?? "?"}", "Win32_Processor");
            device.Fact("Manufacturer", Value(row, "Manufacturer"), "Win32_Processor");
            device.Fact("Socket", Value(row, "SocketDesignation"), "Win32_Processor");
            device.Fact("Reported maximum clock", (Value(row, "MaxClockSpeed") ?? "Unknown") + " MHz", "Win32_Processor");
            device.Signal("Thermal and limit status unknown", "High usage or temperature alone does not establish throttling. No reliable limit-reason provider is mapped to this processor.", HardwareHealthState.Unknown, "Windows inventory");
            devices.Add(device);
        });
    }

    private static void CollectGraphics(List<HardwareDeviceBuilder> devices, List<string> status)
    {
        var index = 0;
        Read(CimNamespace, "SELECT * FROM Win32_VideoController", "Graphics adapters", status, row =>
        {
            index++;
            var pnp = Value(row, "PNPDeviceID");
            var name = Value(row, "Name") ?? $"Graphics adapter {index}";
            var device = new HardwareDeviceBuilder($"gpu/{pnp ?? index.ToString(CultureInfo.InvariantCulture)}", "Graphics", name,
                pnp ?? "Windows display adapter without a PnP instance ID",
                pnp is null ? HardwareMatchConfidence.Unmapped : HardwareMatchConfidence.Verified) { PnpId = pnp };
            device.Fact("Driver version", Value(row, "DriverVersion"), "Win32_VideoController");
            device.Fact("Adapter memory (reported)", Value(row, "AdapterRAM"), "Win32_VideoController");
            device.Signal("Vendor sensors not mapped", "Windows adapter identity has not been verified against a vendor sensor device; sensor readings remain separate.", HardwareHealthState.Unknown, "Identity map");
            devices.Add(device);
        });
    }

    private static void CollectNetwork(List<HardwareDeviceBuilder> devices, List<string> status)
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(item => item.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
        Read(CimNamespace, "SELECT * FROM Win32_NetworkAdapter WHERE PhysicalAdapter = True", "Network adapters", status, row =>
        {
            var guid = Value(row, "GUID");
            var pnp = Value(row, "PNPDeviceID");
            var name = Value(row, "Name") ?? "Network adapter";
            var device = new HardwareDeviceBuilder($"network/{guid ?? pnp ?? name}", "Network", name,
                guid ?? pnp ?? "Adapter identity unavailable", guid is null && pnp is null
                    ? HardwareMatchConfidence.Unmapped : HardwareMatchConfidence.Verified) { PnpId = pnp, NetworkId = guid };
            device.Fact("Manufacturer", Value(row, "Manufacturer"), "Win32_NetworkAdapter");
            device.Fact("Service", Value(row, "ServiceName"), "Win32_NetworkAdapter");
            if (guid is not null && interfaces.TryGetValue(guid.Trim('{', '}'), out var live))
            {
                device.Fact("Link state", live.OperationalStatus.ToString(), ".NET NetworkInterface");
                device.Fact("Reported link speed", live.Speed > 0 ? $"{live.Speed / 1_000_000d:F0} Mb/s" : "Unavailable", ".NET NetworkInterface");
            }
            devices.Add(device);
        });
    }

    private static void CollectBatteries(List<HardwareDeviceBuilder> devices, List<string> status)
    {
        var full = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase);
        Read(BatteryNamespace, "SELECT * FROM BatteryFullChargedCapacity", "Battery full-charge capacity", status, row =>
        {
            if (Value(row, "InstanceName") is { } id && Number(row, "FullChargedCapacity") is { } capacity) full[id] = capacity;
        });
        Read(BatteryNamespace, "SELECT * FROM BatteryStaticData", "Battery identity", status, row =>
        {
            if (Value(row, "InstanceName") is not { } id) return;
            var name = Value(row, "DeviceName") ?? "Battery pack";
            var device = new HardwareDeviceBuilder($"battery/{id}", "Battery", name, id, HardwareMatchConfidence.Verified);
            device.Fact("Manufacturer", Value(row, "ManufactureName"), "BatteryStaticData");
            var capabilities = Number(row, "Capabilities") ?? 0;
            if ((capabilities & 0x40000000) != 0)
            {
                device.Fact("Capacity units", "Relative; Wh comparison unavailable", "BatteryStaticData");
            }
            else
            {
                var design = Number(row, "DesignedCapacity");
                device.Fact("Design / full-charge capacity", $"{MilliwattHours(design)} / {MilliwattHours(full.GetValueOrDefault(id))}", "BatteryStaticData / BatteryFullChargedCapacity");
                if (design is > 0 && full.TryGetValue(id, out var current) && current > 0)
                {
                    var health = 100d * current / design.Value;
                    device.Fact("Capacity retention", $"{health:F1}%", "Battery capacities");
                    if (health < 80)
                        device.Signal("Reduced full-charge capacity", $"Full-charge capacity is {health:F1}% of reported design capacity.", HardwareHealthState.Attention, "Battery capacities");
                }
            }
            devices.Add(device);
        });
    }

    private static void CollectPlatform(List<HardwareDeviceBuilder> devices, List<string> status)
    {
        Read(CimNamespace, "SELECT * FROM Win32_BIOS", "Firmware", status, row =>
        {
            var name = Value(row, "Manufacturer") ?? "System firmware";
            var device = new HardwareDeviceBuilder("platform/firmware", "Platform", name + " firmware",
                "System BIOS/UEFI", HardwareMatchConfidence.Verified);
            device.Fact("BIOS version", Value(row, "SMBIOSBIOSVersion"), "Win32_BIOS");
            device.Fact("Release date", Value(row, "ReleaseDate"), "Win32_BIOS");
            devices.Add(device);
        });
    }

    private static void AttachPnpAndDrivers(List<HardwareDeviceBuilder> devices, List<string> status)
    {
        var byPnp = devices.Where(item => item.PnpId is not null).ToLookup(item => item.PnpId!, StringComparer.OrdinalIgnoreCase);
        Read(CimNamespace, "SELECT PNPDeviceID,ConfigManagerErrorCode,Present FROM Win32_PnPEntity", "PnP status", status, row =>
        {
            if (Value(row, "PNPDeviceID") is not { } id || row["Present"] is false) return;
            var code = Number(row, "ConfigManagerErrorCode");
            if (code is null) return;
            foreach (var device in byPnp[id])
            {
                device.Fact("Device Manager code", code.Value.ToString(CultureInfo.InvariantCulture), "Win32_PnPEntity");
                device.Signal(code == 0 ? "No PnP problem reported" : code == 22 ? "Device disabled" : $"Device Manager code {code}",
                    code == 0 ? "Windows reports no configuration problem for this device. This does not test the hardware." :
                    code == 22 ? "This device is disabled; this may be intentional." : "Windows reports a device configuration or start problem.",
                    code == 0 ? HardwareHealthState.NoProblemObserved : code == 22 ? HardwareHealthState.Attention : HardwareHealthState.ReportedProblem,
                    "Win32_PnPEntity");
            }
        });
        Read(CimNamespace, "SELECT DeviceID,DriverVersion,DriverDate,DriverProviderName,InfName FROM Win32_PnPSignedDriver", "Driver metadata", status, row =>
        {
            if (Value(row, "DeviceID") is not { } id) return;
            foreach (var device in byPnp[id])
            {
                device.Fact("Driver", $"{Value(row, "DriverProviderName") ?? "Unknown provider"} · {Value(row, "DriverVersion") ?? "Unknown version"}", "Win32_PnPSignedDriver");
                device.Fact("Driver date / INF", $"{Value(row, "DriverDate") ?? "Unknown"} · {Value(row, "InfName") ?? "Unknown"}", "Win32_PnPSignedDriver");
            }
        });
    }

    internal static void Read(string nameSpace, string query, string label, List<string> status, Action<ManagementBaseObject> consume)
    {
        var count = 0;
        try
        {
            using var searcher = new ManagementObjectSearcher(nameSpace, query,
                new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(8) });
            using var rows = searcher.Get();
            foreach (ManagementBaseObject row in rows)
            {
                using (row) consume(row);
                count++;
            }
            status.Add($"{label}: {count} returned" + (count == 0 ? "; source may be unsupported" : string.Empty));
        }
        catch (Exception ex) when (IsSourceException(ex))
        {
            status.Add($"{label}: unavailable / partial ({ex.GetType().Name})");
        }
    }

    internal static string? Value(ManagementBaseObject row, string property)
    {
        try { return row.Properties[property]?.Value?.ToString()?.Trim() is { Length: > 0 } value ? value : null; }
        catch (ManagementException) { return null; }
    }

    internal static ulong? Number(ManagementBaseObject row, string property)
        => ulong.TryParse(Value(row, property), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;

    internal static string Gigabytes(ulong? bytes) => bytes is { } value ? $"{value / 1073741824d:F1} GiB" : "Unavailable";
    private static string MilliwattHours(ulong? value) => value is > 0 ? $"{value.Value / 1000d:F1} Wh" : "Unavailable";

    private static bool IsSourceException(Exception ex) => ex is ManagementException or UnauthorizedAccessException
        or System.Runtime.InteropServices.COMException or System.Security.SecurityException or IOException
        or InvalidOperationException or ArgumentException or FormatException or OverflowException;
}

internal sealed class HardwareDeviceBuilder(string key, string category, string name, string identity,
    HardwareMatchConfidence confidence)
{
    private readonly List<HardwareFact> _facts = [];
    private readonly List<HardwareSignal> _signals = [];
    public string? PnpId { get; set; }
    public string? NetworkId { get; set; }
    public uint? WindowsDiskNumber { get; set; }
    public string Key => key;
    public void Fact(string label, string? value, string source, HardwareMatchConfidence match = HardwareMatchConfidence.Verified)
    {
        if (!string.IsNullOrWhiteSpace(value)) _facts.Add(new(label, value, source, match));
    }
    public void Signal(string title, string detail, HardwareHealthState state, string source, DateTimeOffset? at = null)
        => _signals.Add(new(title, detail, state, source, at));
    public HardwareDevice Build() => new(key, category, name, identity, confidence, _facts.ToArray(), _signals.ToArray());
}
