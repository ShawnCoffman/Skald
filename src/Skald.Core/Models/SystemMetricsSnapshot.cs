namespace Skald.Core.Models;

public sealed record SystemMetricsSnapshot(
    DateTimeOffset Timestamp,
    CpuMetric Cpu,
    MemoryMetric Memory,
    DiskMetric Disk,
    IReadOnlyList<ProcessMetric> Processes)
{
    public PowerMetric Power { get; init; } = new(PowerSource.Unknown, null, null, MetricAvailability.Unsupported("Power telemetry has not been collected."));

    public NetworkMetric Network { get; init; } = new(0, 0, 0, MetricAvailability.Unsupported("Network telemetry has not been collected."));

    public IReadOnlyList<GpuAdapterMemoryMetric> GpuAdapters { get; init; } = [];

    public IReadOnlyList<GpuDeviceMetric> GpuDevices { get; init; } = [];
    public IReadOnlyList<GpuEngineMetric> GpuEngines { get; init; } = [];
    public bool GpuEnginesAvailable { get; init; }

    public IReadOnlyList<SensorMetric> Sensors { get; init; } = [];
}
