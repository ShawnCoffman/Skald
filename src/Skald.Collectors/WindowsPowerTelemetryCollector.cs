using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Skald.Core.Models;

namespace Skald.Collectors;

// In-box Windows power counters; no driver or external provider is installed or started.
// Power Meter (PMI) and Energy Meter (EMI) both report milliwatts. Energy Meter instances on Intel/AMD platforms are the
// processor's RAPL domains: the package includes the cores and integrated graphics, so domains are never summed (and _Total is skipped).
internal sealed partial class WindowsPowerTelemetryCollector : IDisposable
{
    private readonly List<(string Instance, PerformanceCounter Power, PerformanceCounter? Budget)> _meters = [];
    private readonly List<(string Instance, PerformanceCounter Power, PerformanceCounter Energy)> _domains = [];
    private readonly PerformanceCounter? _performanceLimit;
    private readonly PerformanceCounter? _performanceLimitFlags;
    private readonly HashSet<string> _primedDomains = [];
    private bool _primed;

    public WindowsPowerTelemetryCollector()
    {
        try
        {
            if (PerformanceCounterCategory.Exists("Power Meter"))
                foreach (var instance in new PerformanceCounterCategory("Power Meter").GetInstanceNames())
                    _meters.Add((instance, new PerformanceCounter("Power Meter", "Power", instance, true), Create("Power Meter", "Power Budget", instance)));
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
        try
        {
            if (PerformanceCounterCategory.Exists("Energy Meter"))
                foreach (var instance in new PerformanceCounterCategory("Energy Meter").GetInstanceNames().Where(name => !name.Equals("_Total", StringComparison.OrdinalIgnoreCase)))
                    _domains.Add((instance, new PerformanceCounter("Energy Meter", "Power", instance, true), new PerformanceCounter("Energy Meter", "Energy", instance, true)));
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
        _performanceLimit = Create("Processor Information", "% Performance Limit", "_Total");
        _performanceLimitFlags = Create("Processor Information", "Performance Limit Flags", "_Total");
    }

    public IReadOnlyList<SensorMetric> Sample()
    {
        var result = new List<SensorMetric>();
        foreach (var (instance, power, budget) in _meters)
        {
            result.Add(new SensorMetric($"windows/power-meter/{instance}", instance, "Hardware power meter", "Power", "W",
                Read(power) / 1000, "Windows Power Meter", "Unidentified hardware domain", "Not attributed to CPU package or whole system."));
            if (budget is not null && Read(budget) is > 0 and var watts)
                result.Add(new SensorMetric($"windows/power-meter/{instance}/budget", instance, "Power budget", "Limit", "W",
                    watts / 1000, "Windows Power Meter", "Power budget", "Budget configured for the supplies this meter monitors."));
        }
        foreach (var (instance, power, energy) in _domains)
        {
            // A domain whose cumulative energy is zero is exposed but not implemented by this processor (for example DRAM on client parts).
            long accumulated;
            try { accumulated = energy.RawValue; }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { continue; }
            if (accumulated <= 0) continue;
            // Power is an average over the interval since the previous read, so the first read only primes the counter.
            var milliwatts = Read(power);
            var watts = _primedDomains.Add(instance) ? null : milliwatts / 1000;
            var (device, name, scope, note) = Describe(instance);
            result.Add(new SensorMetric($"windows/energy-meter/{instance}", device, name, "Power", "W", watts, "Windows Energy Meter (RAPL)", scope, note));
        }
        var limit = Read(_performanceLimit);
        if (_performanceLimit is not null)
            result.Add(new SensorMetric("windows/cpu/performance-limit", "CPU", "Performance limit", "Limit", "%", _primed ? limit : null,
                "Windows Processor Information counters", "CPU performance limit",
                "Share of maximum frequency firmware currently allows. Below 100% means the platform is capping the processor (power, current or thermal); Windows does not say which PL1/PL2 limit applies."));
        var flags = Read(_performanceLimitFlags);
        if (_performanceLimitFlags is not null)
            result.Add(new SensorMetric("windows/cpu/performance-limit-flags", "CPU", "Performance limit flags", "Limit", "bitmask", _primed ? flags : null,
                "Windows Processor Information counters", "CPU performance limit", "0 means no limit reason is reported; a nonzero bit mask means firmware reports one."));
        _primed = true;
        return result;
    }

    internal static (string Device, string Name, string Scope, string Note) Describe(string instance)
    {
        var match = RaplInstance().Match(instance);
        if (!match.Success)
            return (instance, "Energy meter channel", "Unidentified hardware domain", "Windows Energy Meter channel without a recognized RAPL domain; not attributed to CPU package or whole system.");
        var device = $"CPU {int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)}";
        const string Modeled = "Processor-reported running average power (RAPL), not a wall measurement.";
        return match.Groups[2].Value.ToUpperInvariant() switch
        {
            "PKG" => (device, "CPU package power", "CPU package", $"{Modeled} Includes cores and integrated graphics."),
            "PP0" => (device, "CPU cores power", "CPU cores", $"{Modeled} Part of package power; never add to it."),
            "PP1" => (device, "Integrated graphics power", "CPU integrated graphics", $"{Modeled} Part of package power; never add to it."),
            "DRAM" => (device, "DRAM power", "DRAM", $"{Modeled} Memory attached to this package; separate from package power."),
            "PSYS" => (device, "Platform power (PSys)", "Platform", $"{Modeled} SoC platform domain on some laptops; not verified whole-system or wall power."),
            var other => (device, $"RAPL {other} power", "Unidentified hardware domain", Modeled)
        };
    }

    [GeneratedRegex(@"^RAPL_Package(\d+)_(\w+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RaplInstance();

    private static PerformanceCounter? Create(string category, string counter, string instance)
    {
        try { return new PerformanceCounter(category, counter, instance, true); }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { return null; }
    }

    private static double? Read(PerformanceCounter? counter)
    {
        try { return counter?.NextValue() is { } value && float.IsFinite(value) && value >= 0 ? value : null; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    public void Dispose()
    {
        foreach (var meter in _meters) { meter.Power.Dispose(); meter.Budget?.Dispose(); }
        foreach (var domain in _domains) { domain.Power.Dispose(); domain.Energy.Dispose(); }
        _performanceLimit?.Dispose();
        _performanceLimitFlags?.Dispose();
    }
}
