using Skald.Core.Models;
namespace Skald.Diagnostics;

public sealed class WindowDiagnosticAnalyzer
{
    public static IReadOnlyList<DiagnosticFinding> Analyze(IReadOnlyList<SystemMetricsSnapshot> samples, TimeSpan? window = null)
    {
        var findings = new List<DiagnosticFinding>();
        if (samples.Count == 0) return findings;
        var duration = window ?? TimeSpan.FromSeconds(60);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero);
        var end = samples.Max(sample => sample.Timestamp);
        var ordered = samples.Where(sample => sample.Timestamp >= end - duration).OrderBy(sample => sample.Timestamp).DistinctBy(sample => sample.Timestamp).ToArray();
        Add("Sustained CPU saturation", "CPU utilization remained at or above 90%.", s => s.Cpu.Availability.IsSupported && s.Cpu.UtilizationPercent >= 90);
        Add("Sustained low available memory", "Physical occupancy was at least 90% and available memory at most 1 GB. This does not establish paging pressure.", s => s.Memory.Availability.IsSupported && s.Memory.UsedPercent >= 90 && s.Memory.AvailableBytes <= 1024UL * 1024 * 1024);
        Add("Sustained disk activity", "Disk active time remained at or above 90%. Latency and queue depth are not yet assessed.", s => s.Disk.Availability.IsSupported && s.Disk.ActiveTimePercent >= 90);
        var coreIds = ordered.SelectMany(s => s.Cpu.LogicalProcessors).Select(core => core.Id).Distinct();
        foreach (var coreId in coreIds)
            Add($"Sustained logical-processor saturation ({coreId})", "One logical processor remained at or above 90%; total CPU can still be low.", s => s.Cpu.LogicalProcessors.Any(core => core.Id == coreId && core.UtilizationPercent >= 90));
        Add("Sustained processor queue", "Runnable work queued while CPUs were busy; a thread trace is needed to explain the waits.",
            s => s.Cpu.Availability.IsSupported && s.Cpu.ProcessorQueueLength is { } queue && queue >= Math.Max(2, s.Cpu.LogicalProcessorCount) && s.Cpu.UtilizationPercent >= 80);
        Add("Elevated DPC time", "Deferred procedure calls consumed CPU time; a trace is needed to identify the driver.", s => s.Cpu.DpcPercent is >= 10);
        Add("Commit near limit with hard-page reads", "Committed memory approached its limit while hard-page reads were observed; reads can include file-backed pages.",
            s => s.Memory.CommitBytes is { } commit && s.Memory.CommitLimitBytes is { } limit && limit > 0
                && commit >= limit * 0.9 && s.Memory.PageReadsPerSecond is >= 10);
        foreach (var diskName in ordered.SelectMany(s => s.Disk.PhysicalDisks).Select(disk => disk.Name).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Add($"High read latency · {diskName}", "Completed reads had high sampled average latency; this does not identify the requesting process.",
                s => s.Disk.PhysicalDisks.Any(d => d.Name.Equals(diskName, StringComparison.OrdinalIgnoreCase) && d.ReadLatencyMilliseconds is >= 50 && d.ReadsPerSecond is >= 1), 10);
            Add($"High write latency · {diskName}", "Completed writes had high sampled average latency; inspect storage events and an I/O trace.",
                s => s.Disk.PhysicalDisks.Any(d => d.Name.Equals(diskName, StringComparison.OrdinalIgnoreCase) && d.WriteLatencyMilliseconds is >= 50 && d.WritesPerSecond is >= 1), 10);
        }
        Add("Sustained TCP retransmissions", "Windows reported retransmitted TCP segments; this does not attribute a cause to an adapter or remote endpoint.",
            s => s.Network.TcpRetransmitsPerSecond is >= 5);
        foreach (var adapter in ordered.SelectMany(s => s.Network.Interfaces).DistinctBy(item => item.Id))
        {
            var count = ordered.SelectMany(s => s.Network.Interfaces).Where(item => item.Id == adapter.Id)
                .Sum(item => item.NewReceiveErrors.GetValueOrDefault() + item.NewSendErrors.GetValueOrDefault()
                    + item.NewReceiveDiscards.GetValueOrDefault() + item.NewSendDiscards.GetValueOrDefault());
            if (count > 0) findings.Add(new DiagnosticFinding($"Interface errors or discards · {adapter.Name}", PressureState.Elevated,
                "Windows reported new interface errors or discards in this window; inspect the adapter and network path.",
                [$"New errors/discards: {count}", $"Interface ID: {adapter.Id}"]));
        }
        return findings;

        void Add(string title, string explanation, Func<SystemMetricsSnapshot, bool> matches, double minimumSeconds = 30)
        {
            DateTimeOffset? start = null, previous = null, longestStart = null, longestEnd = null;
            double longest = 0;
            foreach (var sample in ordered)
            {
                if (!matches(sample)) { start = null; previous = null; continue; }
                if (previous is null || (sample.Timestamp - previous.Value).TotalSeconds > 6) start = sample.Timestamp;
                var length = (sample.Timestamp - start!.Value).TotalSeconds;
                if (length > longest) { longest = length; longestStart = start; longestEnd = sample.Timestamp; }
                previous = sample.Timestamp;
            }
            if (longest >= minimumSeconds) findings.Add(new DiagnosticFinding(title, PressureState.Elevated, explanation,
                [$"Observed: {longestStart!.Value.ToLocalTime():HH:mm:ss}–{longestEnd!.Value.ToLocalTime():HH:mm:ss} ({longest:F0} seconds)",
                 $"Rule: at least {minimumSeconds:F0} seconds; gaps over 6 seconds break a run."]));
        }
    }
}

