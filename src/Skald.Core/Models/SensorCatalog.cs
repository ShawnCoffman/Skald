namespace Skald.Core.Models;

public static class SensorCatalog
{
    public static IReadOnlyList<SensorMetric> GetSensors(SystemMetricsSnapshot snapshot)
    {
        var result = snapshot.Sensors.ToList();
        result.Add(new SensorMetric("windows/cpu/clock", "CPU", "OS reported core average", "Clock", "MHz",
            snapshot.Cpu.ReportedMegahertz, "Windows processor power API", "Reported clock", "Not effective clock; firmware may report a nominal value."));
        if (!result.Any(sensor => sensor.Scope == "CPU package" && sensor.Kind == "Power"))
            result.Add(new SensorMetric("cpu/package/unavailable", "CPU", "CPU package power", "Power", "W", null,
                "No identified provider", "CPU package", "An existing LibreHardwareMonitor/OpenHardwareMonitor WMI provider can supply supported CPU sensors."));
        result.Add(new SensorMetric("cpu/effective/unavailable", "CPU", "Effective clock", "Clock", "MHz", null,
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
