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
    IReadOnlyList<SessionEvent> Events);
