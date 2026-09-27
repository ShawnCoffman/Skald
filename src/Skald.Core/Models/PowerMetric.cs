namespace Skald.Core.Models;

public sealed record PowerMetric(
    PowerSource Source,
    int? BatteryPercent,
    TimeSpan? EstimatedRemaining,
    MetricAvailability Availability,
    double? CpuPackageWatts = null,
    double? CpuPowerLimitWatts = null,
    double? BatteryDischargeWatts = null,
    MetricAvailability? CpuPackagePowerAvailability = null)
{
    public bool HasBattery => BatteryPercent.HasValue;

    public MetricAvailability CpuPackageAvailability => CpuPackagePowerAvailability
        ?? (CpuPackageWatts.HasValue ? MetricAvailability.Supported : MetricAvailability.Unsupported("CPU package power is unavailable."));

    public double? BatteryDesignCapacityWh { get; init; }
    public double? BatteryFullChargeCapacityWh { get; init; }
    public double? BatteryRemainingCapacityWh { get; init; }
    public int? DisplayBrightnessPercent { get; init; }
    public bool? DisplayActive { get; init; }
    public double? BatteryHealthPercent => BatteryDesignCapacityWh is > 0 && BatteryFullChargeCapacityWh is { } full
        ? 100 * full / BatteryDesignCapacityWh : null;
}
