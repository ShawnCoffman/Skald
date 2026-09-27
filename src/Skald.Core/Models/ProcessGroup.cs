namespace Skald.Core.Models;

public sealed record ProcessGroup(
    string Key,
    string Name,
    bool IsApplication,
    IReadOnlyList<ProcessMetric> Processes)
{
    public double? CpuPercent => Sum(Processes.Select(process => process.CpuAvailable ? process.CpuPercent : (double?)null));
    // Processes sharing the same hardware engine can each consume a slice of it.
    // Sum those slices, then display the busiest engine as Task Manager does.
    public double? GpuPercent => Processes.Any(process => process.GpuEngines is not null)
        ? Processes.SelectMany(process => process.GpuEngines?.AsEnumerable() ?? Enumerable.Empty<KeyValuePair<string, double>>())
            .GroupBy(reading => reading.Key, StringComparer.OrdinalIgnoreCase)
            .Select(engine => Math.Clamp(engine.Sum(reading => reading.Value), 0, 100))
            .DefaultIfEmpty(0)
            .Max()
        : null;
    public double? GpuDedicatedMegabytes => Sum(Processes.Select(process => process.GpuDedicatedMegabytes));
    public double? GpuSharedMegabytes => Sum(Processes.Select(process => process.GpuSharedMegabytes));
    public double? WorkingSetMegabytes => Sum(Processes.Select(process => process.WorkingSetAvailable ? process.WorkingSetMegabytes : (double?)null));
    public double? PrivateMemoryMegabytes => Sum(Processes.Select(process => process.PrivateMemoryAvailable ? process.PrivateMemoryMegabytes : (double?)null));
    public double? ReadBytesPerSecond => Sum(Processes.Select(process => process.ReadBytesPerSecond));
    public double? WriteBytesPerSecond => Sum(Processes.Select(process => process.WriteBytesPerSecond));
    public long? ThreadCount => Sum(Processes.Select(process => (long?)process.ThreadCount));
    public long? HandleCount => Sum(Processes.Select(process => (long?)process.HandleCount));
    public double? PeakWorkingSetMegabytes => Sum(Processes.Select(process => process.PeakWorkingSetMegabytes));
    public TimeSpan? TotalCpuTime => Sum(Processes.Select(process => process.TotalCpuTime));
    public long? PageFaultCount => Sum(Processes.Select(process => (long?)process.PageFaultCount));
    public int PrimaryProcessId => Processes.FirstOrDefault(process => process.HasVisibleWindow)?.ProcessId ?? Processes[0].ProcessId;

    public string? UserName
    {
        get
        {
            var users = Processes.Select(process => process.UserName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return users.Length == 1 ? users[0] : null;
        }
    }

    private static double? Sum(IEnumerable<double?> values)
    {
        var available = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return available.Length == 0 ? null : available.Sum();
    }

    private static long? Sum(IEnumerable<long?> values)
    {
        var available = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return available.Length == 0 ? null : available.Sum();
    }

    private static TimeSpan? Sum(IEnumerable<TimeSpan?> values)
    {
        var available = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        return available.Length == 0 ? null : TimeSpan.FromTicks(available.Sum(value => value.Ticks));
    }
}

public static class ProcessGrouping
{
    public static IReadOnlyList<ProcessGroup> Create(IEnumerable<ProcessMetric> processes)
        => processes
            .GroupBy(GroupKey, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var members = group.ToArray();
                return new ProcessGroup(
                    group.Key,
                    members[0].Name,
                    members.Any(process => process.HasVisibleWindow),
                    members);
            })
            .ToArray();

    private static string GroupKey(ProcessMetric process)
        => string.IsNullOrWhiteSpace(process.ExecutablePath)
            ? $"name:{process.Name}"
            : $"path:{process.ExecutablePath}";
}
