using System.Management;

namespace Skald.Collectors;

internal sealed record BatteryTelemetry(double? DischargeWatts, double? RemainingWh,
    double? FullChargeWh, double? DesignWh, int? BrightnessPercent, bool? DisplayActive);

internal sealed class BatteryTelemetryCollector
{
    private const uint RelativeCapacityFlag = 0x40000000;
    private DateTimeOffset _lastRead;
    private BatteryTelemetry _last = new(null, null, null, null, null, null);

    public BatteryTelemetry Sample()
    {
        if (DateTimeOffset.UtcNow - _lastRead < TimeSpan.FromSeconds(8)) return _last;
        _lastRead = DateTimeOffset.UtcNow;
        try
        {
            var staticData = ReadStaticData();
            Dictionary<string, uint> full;
            try { full = ReadFullCapacity(); }
            catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
            { full = new(StringComparer.OrdinalIgnoreCase); }
            var status = ReadStatus();
            var usable = status.Where(item => staticData.TryGetValue(item.Name, out var data) && !data.Relative).ToArray();
            var allUsable = status.Count > 0 && usable.Length == status.Count;
            double? discharge = allUsable && usable.All(item => !item.Discharging || item.DischargeRate is > 0 and < 500000)
                ? usable.Sum(item => item.Discharging ? item.DischargeRate : 0) / 1000d : null;
            if (discharge is <= 0) discharge = null;
            var remaining = allUsable && usable.All(item => item.RemainingCapacity is > 0 and < 500000)
                ? usable.Sum(item => item.RemainingCapacity) / 1000d : (double?)null;
            var allFull = allUsable && usable.All(item => full.TryGetValue(item.Name, out var value) && value > 0);
            var fullWh = allFull ? usable.Sum(item => full[item.Name]) / 1000d : (double?)null;
            var allDesign = allUsable && usable.All(item => staticData[item.Name].Design > 0);
            var designWh = allDesign ? usable.Sum(item => staticData[item.Name].Design) / 1000d : (double?)null;
            var display = ReadBrightness();
            _last = new(discharge, remaining, fullWh, designWh, display.Brightness, display.Active);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException
            or InvalidCastException or FormatException or OverflowException or ArgumentException)
        { _last = new(null, null, null, null, null, null); }
        return _last;
    }

    private static Dictionary<string, (uint Design, bool Relative)> ReadStaticData()
    {
        var result = new Dictionary<string, (uint, bool)>(StringComparer.OrdinalIgnoreCase);
        using var searcher = Search("SELECT InstanceName,Capabilities,DesignedCapacity FROM BatteryStaticData");
        using var rows = searcher.Get();
        foreach (ManagementBaseObject row in rows)
        {
            using (row)
            {
                if (row["InstanceName"] is not string name) continue;
                var capabilities = Convert.ToUInt32(row["Capabilities"], System.Globalization.CultureInfo.InvariantCulture);
                var design = Convert.ToUInt32(row["DesignedCapacity"], System.Globalization.CultureInfo.InvariantCulture);
                result[name] = (design, (capabilities & RelativeCapacityFlag) != 0);
            }
        }
        return result;
    }

    private static Dictionary<string, uint> ReadFullCapacity()
    {
        var result = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        using var searcher = Search("SELECT InstanceName,FullChargedCapacity FROM BatteryFullChargedCapacity");
        using var rows = searcher.Get();
        foreach (ManagementBaseObject row in rows)
        {
            using (row)
            {
                if (row["InstanceName"] is string name)
                    result[name] = Convert.ToUInt32(row["FullChargedCapacity"], System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        return result;
    }

    private static List<(string Name, bool Discharging, long DischargeRate, uint RemainingCapacity)> ReadStatus()
    {
        var result = new List<(string, bool, long, uint)>();
        using var searcher = Search("SELECT InstanceName,Discharging,DischargeRate,RemainingCapacity FROM BatteryStatus");
        using var rows = searcher.Get();
        foreach (ManagementBaseObject row in rows)
        {
            using (row)
            {
                if (row["InstanceName"] is not string name) continue;
                result.Add((name, row["Discharging"] is true,
                    Convert.ToInt64(row["DischargeRate"], System.Globalization.CultureInfo.InvariantCulture),
                    Convert.ToUInt32(row["RemainingCapacity"], System.Globalization.CultureInfo.InvariantCulture)));
            }
        }
        return result;
    }

    private static (int? Brightness, bool? Active) ReadBrightness()
    {
        try
        {
            using var searcher = Search("SELECT Active,CurrentBrightness FROM WmiMonitorBrightness");
            using var rows = searcher.Get();
            var found = false;
            foreach (ManagementBaseObject row in rows)
            {
                using (row)
                {
                    found = true;
                    if (row["Active"] is true && row["CurrentBrightness"] is byte brightness) return (brightness, true);
                }
            }
            return (null, found ? false : null);
        }
        catch (Exception ex) when (ex is ManagementException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException) { }
        return (null, null);
    }

    private static ManagementObjectSearcher Search(string query) => new(@"root\wmi", query,
        new System.Management.EnumerationOptions { Timeout = TimeSpan.FromSeconds(5) });
}
