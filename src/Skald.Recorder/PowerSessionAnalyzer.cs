using Skald.Core.Models;

namespace Skald.Recorder;

public sealed record ProcessActivity(string Name, double AverageCpuPercent, double AverageGpuPercent);

// Time-weighted power for one measured domain (a CPU package or a GPU board). Domains are reported separately and never summed.
public sealed record DomainPowerSummary(string SensorId, string Label, string Scope, double AverageWatts, double PeakWatts, double EnergyWh,
    TimeSpan Covered, double CoveragePercent);

public sealed record PowerSessionSummary(TimeSpan Duration, TimeSpan BatteryDuration, int BatterySamples, double? AverageDischargeWatts,
    double? PeakDischargeWatts, double? FullChargeWh, double? RemainingWh, double? BatteryHealthPercent,
    double? AverageCpuPercent, double? AverageGpuEnginePercent, double? AverageDiskActivePercent,
    double? AverageNetworkMegabytesPerSecond, double? AverageBrightnessPercent,
    IReadOnlyList<ProcessActivity> Processes)
{
    public double? EstimatedBatteryEnergyWh { get; init; }
    public IReadOnlyList<DomainPowerSummary> Domains { get; init; } = [];
    public double RunCoveragePercent => Duration > TimeSpan.Zero
        ? Math.Clamp(BatteryDuration.TotalSeconds / Duration.TotalSeconds * 100d, 0, 100) : 0;

    public double? FullChargeRuntimeHours => AverageDischargeWatts is > 0 && FullChargeWh is { } capacity
        ? capacity / AverageDischargeWatts : null;
    public double? RemainingRuntimeHours => AverageDischargeWatts is > 0 && RemainingWh is { } capacity
        ? capacity / AverageDischargeWatts : null;

    public static PowerSessionSummary From(SessionDocument document)
    {
        var samples = document.Samples.OrderBy(sample => sample.Timestamp).ToArray();
        var duration = samples.Length > 1 ? samples[^1].Timestamp - samples[0].Timestamp : TimeSpan.Zero;
        var batterySamples = samples.Where(sample => sample.Power.Source == PowerSource.Battery && sample.Power.BatteryDischargeWatts is > 0).ToArray();
        var (batteryDuration, energyWh) = Integrate(samples, index => samples[index].Power.Source == PowerSource.Battery && samples[index].Power.BatteryDischargeWatts is > 0 ? samples[index].Power.BatteryDischargeWatts : null);
        var analyzed = batterySamples.Length > 0 ? batterySamples : samples;
        var discharge = batterySamples.Select(sample => sample.Power.BatteryDischargeWatts!.Value).ToArray();
        var battery = analyzed.LastOrDefault(sample => sample.Power.BatteryFullChargeCapacityWh is > 0);
        var remaining = analyzed.LastOrDefault(sample => sample.Power.BatteryRemainingCapacityWh is > 0);
        var processActivity = analyzed.SelectMany(sample => sample.Processes.GroupBy(process => process.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => new { Name = group.Key, Cpu = group.Sum(process => process.CpuAvailable ? process.CpuPercent : 0),
                Gpu = group.Sum(process => process.GpuPercent.GetValueOrDefault()) }))
            .GroupBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ProcessActivity(group.Key, group.Sum(item => item.Cpu) / Math.Max(1, analyzed.Length),
                group.Sum(item => item.Gpu) / Math.Max(1, analyzed.Length)))
            .OrderByDescending(item => item.AverageCpuPercent + item.AverageGpuPercent).ToArray();
        return new(duration, batteryDuration, discharge.Length,
            batteryDuration > TimeSpan.Zero ? energyWh / batteryDuration.TotalHours : null,
            discharge.Length > 0 ? discharge.Max() : null,
            battery?.Power.BatteryFullChargeCapacityWh, remaining?.Power.BatteryRemainingCapacityWh,
            battery?.Power.BatteryHealthPercent,
            Average(analyzed.Where(sample => sample.Cpu.Availability.IsSupported).Select(sample => sample.Cpu.UtilizationPercent)),
            Average(analyzed.Where(sample => sample.GpuEnginesAvailable).Select(sample => sample.GpuEngines.Select(engine => engine.UtilizationPercent).DefaultIfEmpty().Max())),
            Average(analyzed.Where(sample => sample.Disk.Availability.IsSupported).Select(sample => sample.Disk.ActiveTimePercent)),
            Average(analyzed.Where(sample => sample.Network.Availability.IsSupported).Select(sample => (sample.Network.ReceiveBytesPerSecond + sample.Network.SendBytesPerSecond) / 1048576d)),
            Average(analyzed.Where(sample => sample.Power.DisplayBrightnessPercent.HasValue).Select(sample => (double)sample.Power.DisplayBrightnessPercent!.Value)),
            processActivity) { EstimatedBatteryEnergyWh = batteryDuration > TimeSpan.Zero ? energyWh : null, Domains = MeasuredDomains(samples, duration) };
    }

    private static DomainPowerSummary[] MeasuredDomains(SystemMetricsSnapshot[] samples, TimeSpan duration)
    {
        var readings = samples.Select(sample => (sample, sensors: SensorCatalog.GetSensors(sample)
            .Where(sensor => sensor.Kind == "Power" && sensor.Scope is "CPU package" or "GPU board").ToDictionary(sensor => sensor.Id))).ToArray();
        return readings.SelectMany(item => item.sensors.Values).DistinctBy(sensor => sensor.Id)
            .Select(sensor =>
            {
                var values = readings.Select(item => item.sensors.TryGetValue(sensor.Id, out var reading) ? reading.Value : null).ToArray();
                var (covered, energy) = Integrate(samples, index => values[index]);
                if (covered <= TimeSpan.Zero) return null;
                return new DomainPowerSummary(sensor.Id, $"{sensor.Device} · {sensor.Name}", sensor.Scope, energy / covered.TotalHours,
                    values.Where(value => value.HasValue).Max()!.Value, energy, covered,
                    duration > TimeSpan.Zero ? Math.Clamp(covered.TotalSeconds / duration.TotalSeconds * 100, 0, 100) : 0);
            })
            .OfType<DomainPowerSummary>().OrderBy(item => item.Scope, StringComparer.Ordinal).ThenBy(item => item.Label, StringComparer.Ordinal).ToArray();
    }

    // Trapezoidal energy across adjacent valid readings. Gaps longer than 2.5 sampling intervals (10–120 s) do not count.
    private static (TimeSpan Covered, double EnergyWh) Integrate(SystemMetricsSnapshot[] samples, Func<int, double?> watts)
    {
        if (samples.Length < 2) return (TimeSpan.Zero, 0);
        var intervals = samples.Zip(samples.Skip(1), (first, second) => (second.Timestamp - first.Timestamp).TotalSeconds)
            .Where(seconds => seconds > 0).Order().ToArray();
        var medianSeconds = intervals.Length == 0 ? 0 : intervals[intervals.Length / 2];
        var maximumGapSeconds = Math.Clamp(medianSeconds * 2.5, 10, 120);
        var covered = TimeSpan.Zero;
        var energyWh = 0d;
        for (var index = 1; index < samples.Length; index++)
        {
            var before = samples[index - 1];
            var after = samples[index];
            var elapsed = after.Timestamp - before.Timestamp;
            if (elapsed <= TimeSpan.Zero || elapsed.TotalSeconds > maximumGapSeconds
                || watts(index - 1) is not { } first || watts(index) is not { } second) continue;
            covered += elapsed;
            energyWh += (first + second) / 2 * elapsed.TotalHours;
        }
        return (covered, energyWh);
    }

    private static double? Average(IEnumerable<double> values)
    {
        var data = values.ToArray();
        return data.Length == 0 ? null : data.Average();
    }
}

public static class PowerSessionComparison
{
    public static string Describe(PowerSessionSummary baseline, PowerSessionSummary current)
    {
        if (baseline.AverageDischargeWatts is not { } before || current.AverageDischargeWatts is not { } after)
            return "Both recordings need battery-discharge readings to compare system draw.";
        if (baseline.BatteryDuration < TimeSpan.FromMinutes(5) || current.BatteryDuration < TimeSpan.FromMinutes(5)
            || baseline.BatterySamples < 30 || current.BatterySamples < 30)
            return "Each comparison recording needs at least five minutes and 30 measured battery-draw samples.";
        var changed = current.Processes.Select(process => new
            {
                process.Name,
                CpuChange = process.AverageCpuPercent - (baseline.Processes.FirstOrDefault(old => old.Name.Equals(process.Name, StringComparison.OrdinalIgnoreCase))?.AverageCpuPercent ?? 0),
                GpuChange = process.AverageGpuPercent - (baseline.Processes.FirstOrDefault(old => old.Name.Equals(process.Name, StringComparison.OrdinalIgnoreCase))?.AverageGpuPercent ?? 0)
            })
            .OrderByDescending(item => item.CpuChange + item.GpuChange).Take(3)
            .Where(item => item.CpuChange > 1 || item.GpuChange > 1).ToArray();
        var processes = changed.Length == 0 ? "No clear increase among sampled top processes."
            : "More active processes: " + string.Join(", ", changed.Select(item => $"{item.Name} (CPU {item.CpuChange:+0.0;-0.0;0} points, GPU {item.GpuChange:+0.0;-0.0;0} points)"));
        return $"Average battery draw {before:F1} → {after:F1} W ({after - before:+0.0;-0.0;0.0} W).\n" +
            $"Battery health {Format(baseline.BatteryHealthPercent, "%")} → {Format(current.BatteryHealthPercent, "%")}.\n" +
            $"CPU {Format(baseline.AverageCpuPercent, "%")} → {Format(current.AverageCpuPercent, "%")}; GPU busiest engine {Format(baseline.AverageGpuEnginePercent, "%")} → {Format(current.AverageGpuEnginePercent, "%")}.\n" +
            $"Disk active {Format(baseline.AverageDiskActivePercent, "%")} → {Format(current.AverageDiskActivePercent, "%")}; network {Format(baseline.AverageNetworkMegabytesPerSecond, "MB/s")} → {Format(current.AverageNetworkMegabytesPerSecond, "MB/s")}; brightness {Format(baseline.AverageBrightnessPercent, "%")} → {Format(current.AverageBrightnessPercent, "%")}.\n" +
            processes + "\nThese are correlations in two recordings, not measured per-process watts or proof of cause.";
    }

    private static string Format(double? value, string unit) => value is { } number ? $"{number:F1} {unit}" : "Unavailable";
}
