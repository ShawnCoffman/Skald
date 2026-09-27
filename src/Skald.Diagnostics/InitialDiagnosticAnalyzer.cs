using Skald.Core.Interfaces;
using Skald.Core.Models;

namespace Skald.Diagnostics;

public sealed class InitialDiagnosticAnalyzer : IDiagnosticAnalyzer
{
    public IReadOnlyList<DiagnosticFinding> Analyze(SystemMetricsSnapshot snapshot)
    {
        var findings = new List<DiagnosticFinding>();

        // High occupancy alone is not evidence of pressure: available memory may still be ample.
        if (snapshot.Memory.Availability.IsSupported &&
            snapshot.Memory.UsedPercent >= 90 &&
            snapshot.Memory.AvailableBytes <= 1024UL * 1024 * 1024)
        {
            findings.Add(new DiagnosticFinding(
                "Low available physical memory",
                snapshot.Memory.AvailableBytes <= 512UL * 1024 * 1024 ? PressureState.High : PressureState.Elevated,
                "Available physical memory is low. This is a warning, not proof of paging pressure; commit and hard-fault rates are not collected yet.",
                [$"Physical memory used: {snapshot.Memory.UsedPercent:F1}%", $"Available memory: {FormatBytes(snapshot.Memory.AvailableBytes)}"]));
        }

        if (snapshot.Cpu.Availability.IsSupported && snapshot.Cpu.UtilizationPercent >= 90)
        {
            findings.Add(new DiagnosticFinding(
                "CPU saturation",
                snapshot.Cpu.UtilizationPercent >= 98 ? PressureState.High : PressureState.Elevated,
                "The processors are busy enough that CPU work may be constraining responsiveness.",
                [$"Total CPU utilization: {snapshot.Cpu.UtilizationPercent:F1}%", $"Logical processors: {snapshot.Cpu.LogicalProcessorCount}"]));
        }

        if (snapshot.Disk.Availability.IsSupported && snapshot.Disk.ActiveTimePercent >= 90)
        {
            findings.Add(new DiagnosticFinding(
                "Disk activity",
                snapshot.Disk.ActiveTimePercent >= 99 ? PressureState.High : PressureState.Elevated,
                "The physical disk is active for most of the sample interval. Latency and queue depth will be added to the analyzer in the recorder milestone.",
                [$"Disk active time: {snapshot.Disk.ActiveTimePercent:F1}%", $"Transfers/sec: {snapshot.Disk.TransfersPerSecond:F1}"]));
        }

        return findings;
    }

    private static string FormatBytes(ulong bytes)
    {
        var value = bytes;
        var suffix = "B";
        if (value >= 1024) { value /= 1024; suffix = "KB"; }
        if (value >= 1024) { value /= 1024; suffix = "MB"; }
        if (value >= 1024) { value /= 1024; suffix = "GB"; }
        return $"{value:N0} {suffix}";
    }
}
