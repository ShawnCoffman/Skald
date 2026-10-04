using System.Management;

namespace Skald.Triage;

// Win32_PnPSignedDriver is slow (seconds) and lists every device with a driver, so it runs once per scan on a background task.
public static class DriverInventoryCollector
{
    public static Task<DriverInventory> CollectAsync() => Task.Run(Collect);

    private static DriverInventory Collect()
    {
        var drivers = new List<DriverRecord>();
        var status = new List<string>();
        var complete = true;
        try
        {
            using var searcher = new ManagementObjectSearcher(HardwareInventoryCollector.CimNamespace,
                "SELECT DeviceID,DeviceName,DeviceClass,DriverVersion,DriverDate,DriverProviderName,InfName,IsSigned,Signer FROM Win32_PnPSignedDriver",
                new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(90) });
            using var rows = searcher.Get();
            foreach (ManagementBaseObject row in rows)
            {
                using (row)
                {
                    var id = HardwareInventoryCollector.Value(row, "DeviceID");
                    var version = HardwareInventoryCollector.Value(row, "DriverVersion");
                    if (id is null || version is null) continue;
                    drivers.Add(new(id, HardwareInventoryCollector.Value(row, "DeviceName") ?? id, HardwareInventoryCollector.Value(row, "DeviceClass") ?? "Other",
                        version, DriverComparison.ParseCimDate(HardwareInventoryCollector.Value(row, "DriverDate")),
                        HardwareInventoryCollector.Value(row, "DriverProviderName") ?? "Unknown", HardwareInventoryCollector.Value(row, "InfName") ?? "",
                        row["IsSigned"] is bool signed ? signed : null, HardwareInventoryCollector.Value(row, "Signer")));
                }
            }
            complete = drivers.Count > 0;
            status.Add($"Driver inventory: {drivers.Count} drivers" + (drivers.Count == 0 ? "; source may be unsupported" : string.Empty));
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            complete = false;
            status.Add($"Driver inventory: unavailable / partial after {drivers.Count} drivers ({ex.GetType().Name})");
        }
        return new(DateTimeOffset.Now, drivers, status) { Complete = complete };
    }
}
