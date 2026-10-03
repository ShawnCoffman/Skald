using Skald.Core.Models;

namespace Skald.Recorder;

public sealed record SessionMetadata(
    Guid SessionId,
    string ProductName,
    string ProductVersion,
    string MachineName,
    string OperatingSystem,
    DateTimeOffset StartedAt,
    DateTimeOffset EndedAt,
    int SampleIntervalSeconds,
    bool Recovered = false);

public sealed record SessionDocument(
    SessionMetadata Metadata,
    IReadOnlyList<SystemMetricsSnapshot> Samples,
    IReadOnlyList<SessionEvent> Events)
{
    // Captured on the recorded machine, so a recording replayed elsewhere never borrows the viewer's identity or Windows reports.
    // Null in recordings made before Skald saved them.
    public SessionMachine? Machine { get; init; }
    public SessionWindowsEvidence? WindowsEvidence { get; init; }
    public IReadOnlyList<SessionTrace> Traces { get; init; } = [];
    // True for an export with computer, user and profile names removed.
    public bool Redacted { get; init; }
}
