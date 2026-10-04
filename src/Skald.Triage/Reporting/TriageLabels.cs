namespace Skald.Triage;

public static class TriageLabels
{
    // Same words in the app, the console report and the HTML report, so a tech reads one vocabulary everywhere.
    public static string Status(TriageCheck check) => check.Status switch
    {
        CheckStatus.Found => check.Domain switch
        {
            CheckDomain.HardwareDriverFirmware => check.Minor ? "Minor finding" : "EVIDENCE FOUND",
            CheckDomain.Software => "FOUND",
            CheckDomain.DeviceSettings => check.Minor ? "Noted" : "SETTING FOUND",
            _ => "NOTED"
        },
        CheckStatus.NotFound => check.Domain == CheckDomain.HardwareDriverFirmware ? "Nothing found" : "Nothing noted",
        CheckStatus.CouldNotCheck => "COULD NOT CHECK",
        _ => "Not applicable"
    };

    public static string Domain(CheckDomain domain) => domain switch
    {
        CheckDomain.HardwareDriverFirmware => "Hardware, driver and firmware evidence",
        CheckDomain.Software => "Application-side evidence",
        CheckDomain.DeviceSettings => "Windows settings that can make a working device look broken (not part of the hardware verdict)",
        _ => "Context (not part of the hardware verdict)"
    };

    // Report sections in reading order: the verdict's evidence, then what can explain a symptom without a fault, then the rest.
    public static readonly IReadOnlyList<CheckDomain> DomainOrder = [CheckDomain.HardwareDriverFirmware, CheckDomain.DeviceSettings, CheckDomain.Software, CheckDomain.Context];

    public const string UpdateSection = "Driver and BIOS updates: what the evidence says";

    public static string Update(UpdateLean lean) => lean switch
    {
        UpdateLean.PointsHere => "EVIDENCE POINTS HERE",
        UpdateLean.CouldNotAssess => "COULD NOT ASSESS",
        _ => "Nothing points here"
    };

    public static string Outcome(TriageOutcome outcome) => outcome switch
    {
        TriageOutcome.HardwareEvidenceFound => "EVIDENCE FOUND",
        TriageOutcome.MinorFindings => "MINOR FINDINGS ONLY",
        TriageOutcome.NoHardwareEvidence => "NO EVIDENCE FOUND",
        _ => "INCOMPLETE"
    };

    // Exit codes for scripted use: 0 nothing significant (clear or minor only), 1 evidence found, 2 incomplete.
    public static int ExitCode(TriageOutcome outcome) => outcome switch
    {
        TriageOutcome.NoHardwareEvidence or TriageOutcome.MinorFindings => 0,
        TriageOutcome.HardwareEvidenceFound => 1,
        _ => 2
    };
}
