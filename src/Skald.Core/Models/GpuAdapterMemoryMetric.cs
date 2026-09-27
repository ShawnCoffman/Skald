namespace Skald.Core.Models;

public sealed record GpuAdapterMemoryMetric(
    string AdapterKey,
    double? DedicatedUsedMegabytes,
    double? SharedUsedMegabytes);
