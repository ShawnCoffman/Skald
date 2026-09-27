namespace Skald.Core.Models;

public sealed record GpuEngineMetric(string EngineKey, string EngineType, double UtilizationPercent);
