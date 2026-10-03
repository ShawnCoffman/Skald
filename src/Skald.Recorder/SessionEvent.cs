namespace Skald.Recorder;

public enum SessionEventType
{
    UserMarker,
    CpuSaturation,
    MemoryPressure,
    DiskActivity,
    ProcessLaunch,
    ProcessExit,
    PowerSourceChanged,
    NetworkChanged,
    DeepTraceSaved
}

public sealed record SessionEvent(
    DateTimeOffset Timestamp,
    SessionEventType Type,
    string? Note = null)
{
    // Set on process launch and exit events, so replay can line an exit up with the Windows crash or hang report for the same program.
    public string? ProcessName { get; init; }
    public int? ProcessId { get; init; }
    public bool? HadWindow { get; init; }
}
