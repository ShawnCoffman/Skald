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
    NetworkChanged
}

public sealed record SessionEvent(
    DateTimeOffset Timestamp,
    SessionEventType Type,
    string? Note = null);
