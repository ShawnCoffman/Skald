using Skald.Core.Models;

namespace Skald.Triage;

public enum CheckStatus { NotFound, Found, CouldNotCheck, NotApplicable }

// HardwareDriverFirmware checks decide the verdict. Software, DeviceSettings and Context checks are shown beside it as hand-off evidence.
// DeviceSettings holds Windows settings (airplane mode, a radio switched off, a disabled device) that make working hardware look broken.
public enum CheckDomain { HardwareDriverFirmware, Software, Context, DeviceSettings }

public sealed record TriageCheck(string Id, string Title, CheckDomain Domain, CheckStatus Status, string Summary,
    IReadOnlyList<string> Details, string? Limit = null, bool Minor = false)
{
    // DeviceSettings checks only: what the setting blocks, in the words the hand-off headline uses ("Wi-Fi or networking", "the camera").
    public string? Blocks { get; init; }
}

public enum TriageOutcome { HardwareEvidenceFound, MinorFindings, NoHardwareEvidence, Incomplete }

// Coverage is set when an event log was only partly read, so anything quoting the verdict can say how far back it really looked.
public sealed record TriageVerdict(TriageOutcome Outcome, string Headline, string Detail, IReadOnlyList<string> Leads, string? Coverage = null);

public sealed record TriageReport(DateTimeOffset GeneratedAt, int WindowDays, bool Elevated, string ToolVersion,
    IReadOnlyList<InventoryDetail> Machine, TriageVerdict Verdict, IReadOnlyList<TriageCheck> Checks,
    IReadOnlyList<string> NotCovered, IReadOnlyList<string> SourceStatus)
{
    public IEnumerable<TriageCheck> In(CheckDomain domain) => Checks.Where(check => check.Domain == domain);

    // What the evidence says about updating each checked driver and the BIOS, so a tech can see when it supports neither.
    public IReadOnlyList<UpdateNote> UpdateEvidence { get; init; } = [];
}

public sealed record TriageInputs(DateTimeOffset Now, int WindowDays, bool Elevated)
{
    public ReliabilityHistory? Reliability { get; init; }
    public HardwareInventory? Hardware { get; init; }
    public SystemInventory? System { get; init; }
    public CrashDumpInventory? Dumps { get; init; }
    public WindowsUpdateHistory? Updates { get; init; }
    public DriverInventory? Drivers { get; init; }
    public DeviceEvidence? Devices { get; init; }
    public DeviceSettings? Settings { get; init; }
    public DriverSnapshot? Baseline { get; init; }
    public string? BaselineLabel { get; init; }
    // Set when a specific baseline file was asked for, so a missing or unreadable file is reported instead of treated as "first scan".
    public string? BaselineRequested { get; init; }
    public IReadOnlyDictionary<string, DumpHeaderInfo> DumpHeaders { get; init; } = new Dictionary<string, DumpHeaderInfo>();
    public string? BiosVersion { get; init; }
    public DateTimeOffset? BiosReleaseDate { get; init; }
    public IReadOnlyList<InventoryDetail> Machine { get; init; } = [];
    public IReadOnlyList<string> SourceStatus { get; init; } = [];
}
