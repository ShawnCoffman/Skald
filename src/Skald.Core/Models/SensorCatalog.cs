namespace Skald.Core.Models;

public static class SensorCatalog
{
    // NVML clock event (throttle) reasons that mean the board is being held back by power or heat. Idle, application and display clocks are normal.
    private static readonly (ulong Bit, string Label)[] GpuCapReasons =
        [(0x4, "power cap"), (0x8, "hardware slowdown"), (0x20, "software thermal"), (0x40, "hardware thermal"), (0x80, "power brake")];

    public const ulong GpuPowerCapReason = 0x4;
    public const ulong GpuThermalOrHardwareReasons = 0x8 | 0x20 | 0x40 | 0x80;

    public static IReadOnlyList<string> DescribeGpuCapReasons(double? reasons)
        => reasons is { } value and >= 0 ? GpuCapReasons.Where(reason => ((ulong)value & reason.Bit) != 0).Select(reason => reason.Label).ToArray() : [];

    public static IReadOnlyList<SensorMetric> GetSensors(SystemMetricsSnapshot snapshot)
    {
        var result = snapshot.Sensors.ToList();
        result.Add(new SensorMetric("windows/cpu/clock", "CPU", "OS reported core average", "Clock", "MHz",
            snapshot.Cpu.ReportedMegahertz, "Windows processor power API", "Reported clock", "Not effective clock; firmware may report a nominal value."));
        if (!result.Any(sensor => sensor.Scope == "CPU package" && sensor.Kind == "Power"))
            result.Add(new SensorMetric("cpu/package/unavailable", "CPU", "CPU package power", "Power", "W", null,
                "No identified provider", "CPU package", "Windows Energy Meter (RAPL) counters are not exposed on this machine; an existing LibreHardwareMonitor/OpenHardwareMonitor WMI provider can supply supported CPU sensors."));
        result.Add(snapshot.Cpu.EffectiveMegahertz is { } effective
            ? new SensorMetric("cpu/effective/clock", "CPU", "Effective clock (estimate)", "Clock", "MHz", effective,
                "Windows Processor Information counters", "Effective clock", "Base frequency x % Processor Performance across all logical processors. Reflects boost and throttling; it is an estimate, not a per-core measurement.")
            : new SensorMetric("cpu/effective/unavailable", "CPU", "Effective clock", "Clock", "MHz", null,
                "Not collected", "Effective clock"));
        if (!result.Any(sensor => sensor.Scope == "CPU sensor" && sensor.Kind == "Temperature"))
            result.Add(new SensorMetric("cpu/temperature/unavailable", "CPU", "CPU temperature", "Temperature", "°C", null,
                "No identified provider", "CPU sensor"));
        foreach (var (gpu, index) in snapshot.GpuDevices.Select((device, index) => (device, index)))
        {
            var id = gpu.DeviceId ?? $"legacy-{index}-{gpu.Name}";
            result.Add(new SensorMetric($"nvml/{id}/power", gpu.Name, "GPU board power", "Power", "W", gpu.PowerWatts, gpu.Source, "GPU board"));
            result.Add(new SensorMetric($"nvml/{id}/temperature", gpu.Name, "GPU temperature", "Temperature", "°C", gpu.TemperatureCelsius, gpu.Source, "GPU"));
            result.Add(new SensorMetric($"nvml/{id}/graphics-clock", gpu.Name, "Graphics clock", "Clock", "MHz", gpu.GraphicsClockMegahertz, gpu.Source, "GPU graphics"));
            result.Add(new SensorMetric($"nvml/{id}/memory-clock", gpu.Name, "Memory clock", "Clock", "MHz", gpu.MemoryClockMegahertz, gpu.Source, "GPU memory"));
            if (gpu.PowerLimitWatts is not null)
                result.Add(new SensorMetric($"nvml/{id}/power-limit", gpu.Name, "Enforced power limit", "Limit", "W", gpu.PowerLimitWatts, gpu.Source, "GPU power limit",
                    "Board power ceiling the driver currently enforces."));
            if (gpu.ClockLimitReasons is { } reasons)
                result.Add(new SensorMetric($"nvml/{id}/clock-limit-reasons", gpu.Name, "Clock limit reasons", "Limit", "bitmask", reasons, gpu.Source, "GPU clock limit",
                    "NVML clock event reasons. Power cap (0x4), hardware slowdown (0x8), software thermal (0x20), hardware thermal (0x40) and power brake (0x80) mean power or heat is holding clocks back; idle (0x1) is normal."));
            if (gpu.PerformanceState is { } state)
                result.Add(new SensorMetric($"nvml/{id}/performance-state", gpu.Name, "Performance state", "State", "P-state", state, gpu.Source, "GPU",
                    "P0 is maximum performance; higher numbers are lower-power states."));
            if (gpu.FanSpeedPercent is not null)
                result.Add(new SensorMetric($"nvml/{id}/fan", gpu.Name, "Fan speed", "Fan", "%", gpu.FanSpeedPercent, gpu.Source, "GPU",
                    "Percent of maximum fan speed the driver requests; not a measured RPM."));
        }
        if (snapshot.Power.HasBattery)
            result.Add(new SensorMetric("battery/discharge", "Battery", "Battery discharge", "Power", "W", snapshot.Power.BatteryDischargeWatts,
                "Windows battery telemetry", "Battery"));
        // Older sessions may contain an incorrectly attributed generic power meter. Do not relabel it as CPU power.
        if (snapshot.Power.CpuPackageWatts is { } legacy)
            result.Add(new SensorMetric("legacy/power", "Legacy recording", "Unverified power reading", "Power", "W", legacy,
                "Legacy Windows power meter", "Unidentified hardware domain", "Older recordings did not verify sensor scope."));
        return result;
    }
}
