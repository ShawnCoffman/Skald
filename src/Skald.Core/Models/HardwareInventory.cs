namespace Skald.Core.Models;

public enum HardwareHealthState
{
    Unknown,
    NoProblemObserved,
    Attention,
    ReportedProblem
}

public enum HardwareMatchConfidence
{
    Unmapped,
    Probable,
    Verified
}

public sealed record HardwareFact(string Label, string Value, string Source,
    HardwareMatchConfidence Confidence = HardwareMatchConfidence.Verified);

public sealed record HardwareSignal(string Title, string Detail, HardwareHealthState State,
    string Source, DateTimeOffset? ObservedAt = null);

public sealed record HardwareDevice(string Key, string Category, string Name, string Identity,
    HardwareMatchConfidence IdentityConfidence, IReadOnlyList<HardwareFact> Facts,
    IReadOnlyList<HardwareSignal> Signals)
{
    public HardwareHealthState Health => Signals.Any(item => item.State == HardwareHealthState.ReportedProblem)
        ? HardwareHealthState.ReportedProblem
        : Signals.Any(item => item.State == HardwareHealthState.Attention)
            ? HardwareHealthState.Attention
            : Signals.Any(item => item.State == HardwareHealthState.NoProblemObserved)
                ? HardwareHealthState.NoProblemObserved : HardwareHealthState.Unknown;

    public string HealthLabel => Health switch
    {
        HardwareHealthState.ReportedProblem => "Reported problem",
        HardwareHealthState.Attention => "Attention",
        HardwareHealthState.NoProblemObserved => "No problem observed",
        _ => "Unknown"
    };

    public string DisplayName => $"{Name}  ·  {HealthLabel}";
}

public sealed record HardwareInventory(DateTimeOffset CollectedAt, IReadOnlyList<HardwareDevice> Devices,
    IReadOnlyList<HardwareSignal> UnmappedSignals, IReadOnlyList<string> SourceStatus);

public static class HardwareIdentity
{
    // PhysicalDisk counter instances normally begin with the Windows disk number.
    // This is a probable association; the storage provider's own disk/partition number is verified.
    public static bool MatchesDiskCounter(uint diskNumber, string instanceName)
    {
        var prefix = diskNumber.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return instanceName.Equals(prefix, StringComparison.OrdinalIgnoreCase)
            || instanceName.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase);
    }

    public static bool SamePnpDevice(string? first, string? second)
        => !string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second)
            && string.Equals(first.Trim(), second.Trim(), StringComparison.OrdinalIgnoreCase);
}
