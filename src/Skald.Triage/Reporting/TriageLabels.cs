namespace Skald.Triage;

public static class TriageLabels
{
    // Same words in the app, the console report and the HTML report, so a tech reads one vocabulary everywhere.
    public static string Status(TriageCheck check) => check.Status switch
    {
        CheckStatus.Found => check.Domain == CheckDomain.HardwareDriverFirmware ? (check.Minor ? "Minor finding" : "EVIDENCE FOUND") : check.Domain == CheckDomain.Software ? "FOUND" : "NOTED",
        CheckStatus.NotFound => check.Domain == CheckDomain.HardwareDriverFirmware ? "Nothing found" : "Nothing noted",
        CheckStatus.CouldNotCheck => "COULD NOT CHECK",
        _ => "Not applicable"
    };

    public static string Domain(CheckDomain domain) => domain switch
    {
        CheckDomain.HardwareDriverFirmware => "Hardware, driver and firmware evidence",
        CheckDomain.Software => "Application-side evidence",
        _ => "Context (not part of the hardware verdict)"
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
