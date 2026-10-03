namespace Skald.Triage;

public sealed record DriverRecord(string DeviceId, string Name, string Class, string Version, DateTimeOffset? Date,
    string Provider, string Inf, bool? IsSigned);

public sealed record DriverInventory(DateTimeOffset CollectedAt, IReadOnlyList<DriverRecord> Drivers, IReadOnlyList<string> SourceStatus)
{
    // False when the WMI query failed or timed out part-way; a partial list must not be compared or saved as a baseline.
    public bool Complete { get; init; } = true;
}

public sealed record DriverSnapshot(DateTimeOffset CreatedAt, string Computer, string? Model, string? BiosVersion,
    string? OsBuild, IReadOnlyList<DriverRecord> Drivers, string? Label = null);

public enum DriverChangeKind { Added, Removed, Updated, Downgraded, Changed }

public sealed record DriverChange(DriverChangeKind Kind, DriverRecord? Before, DriverRecord? After)
{
    public DriverRecord Subject => After ?? Before!;
    public string Describe() => Kind switch
    {
        DriverChangeKind.Added => $"{Subject.Class} · {Subject.Name}: added {Subject.Provider} {Subject.Version}",
        DriverChangeKind.Removed => $"{Subject.Class} · {Subject.Name}: removed (was {Subject.Provider} {Subject.Version})",
        _ => $"{Subject.Class} · {Subject.Name}: {Kind.ToString().ToLowerInvariant()} {Before!.Version} → {After!.Version} ({After.Provider})"
    };
}
