using System.Diagnostics;
using Skald.Core.Models;

namespace Skald.Collectors;

// ACPI thermal zones exposed by Windows. A zone is a platform sensor (often the board or chassis), not necessarily the CPU die;
// "% Passive Limit" below 100 and a nonzero "Throttle Reasons" mean the platform is asking Windows to limit processor performance.
internal sealed class ThermalZoneCollector : IDisposable
{
    public const string Category = "Thermal Zone Information";

    private readonly List<(string Zone, PerformanceCounter? Temperature, PerformanceCounter? PassiveLimit, PerformanceCounter? ThrottleReasons)> _zones = [];

    public ThermalZoneCollector()
    {
        try
        {
            if (!PerformanceCounterCategory.Exists(Category)) return;
            foreach (var zone in new PerformanceCounterCategory(Category).GetInstanceNames())
                _zones.Add((zone, Create("Temperature", zone), Create("% Passive Limit", zone), Create("Throttle Reasons", zone)));
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
    }

    public IReadOnlyList<SensorMetric> Sample()
    {
        var result = new List<SensorMetric>();
        foreach (var (zone, temperature, passiveLimit, throttleReasons) in _zones)
        {
            // The Temperature counter is whole kelvin; values outside a plausible range mean the zone reports nothing useful.
            var kelvin = Read(temperature);
            double? celsius = kelvin is > 200 and < 500 ? kelvin - 273.15 : null;
            result.Add(new SensorMetric($"windows/thermal-zone/{zone}/temperature", zone, "Thermal zone temperature", "Temperature", "°C", celsius,
                "Windows thermal zone counters", "Thermal zone", "ACPI platform zone; not necessarily the CPU die temperature."));
            result.Add(new SensorMetric($"windows/thermal-zone/{zone}/passive-limit", zone, "Passive thermal limit", "Limit", "%", Read(passiveLimit),
                "Windows thermal zone counters", "Thermal limit", "100% means the platform is not limiting processors; lower values mean passive cooling is throttling them."));
            result.Add(new SensorMetric($"windows/thermal-zone/{zone}/throttle-reasons", zone, "Throttle reasons", "Limit", "bitmask", Read(throttleReasons),
                "Windows thermal zone counters", "Thermal limit", "0 means no throttle reason is reported; a nonzero bit mask means the platform reports throttling."));
        }
        return result;
    }

    public void Dispose()
    {
        foreach (var (_, temperature, passiveLimit, throttleReasons) in _zones)
        {
            temperature?.Dispose();
            passiveLimit?.Dispose();
            throttleReasons?.Dispose();
        }
    }

    private static PerformanceCounter? Create(string counter, string zone)
    {
        try { return new PerformanceCounter(Category, counter, zone, true); }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { return null; }
    }

    private static double? Read(PerformanceCounter? counter)
    {
        try { return counter?.NextValue() is { } value && float.IsFinite(value) && value >= 0 ? value : null; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }
}
