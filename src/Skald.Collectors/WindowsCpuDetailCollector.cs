using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Skald.Core.Models;

namespace Skald.Collectors;

internal sealed class WindowsCpuDetailCollector : IDisposable
{
    private readonly List<(string Id, PerformanceCounter Counter)> _usage = [];
    private readonly PerformanceCounter? _performance;
    private readonly PerformanceCounter? _baseFrequency;
    private bool _primed;

    public WindowsCpuDetailCollector()
    {
        try
        {
            var category = new PerformanceCounterCategory("Processor Information");
            foreach (var id in category.GetInstanceNames().Where(id => !id.Contains("_Total", StringComparison.Ordinal)))
                _usage.Add((id, new PerformanceCounter("Processor Information", "% Processor Time", id, true)));
        }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
        _performance = Create("% Processor Performance");
        _baseFrequency = Create("Processor Frequency");
    }

    private static PerformanceCounter? Create(string counter)
    {
        try { return new PerformanceCounter("Processor Information", counter, "_Total", true); }
        catch (Exception ex) when (ex is InvalidOperationException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { return null; }
    }

    private static double? Read(PerformanceCounter? counter)
    {
        try { return counter?.NextValue() is { } value && float.IsFinite(value) && value > 0 ? value : null; }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { return null; }
    }

    public CpuMetric Enrich(CpuMetric cpu)
    {
        var clocks = new ProcessorPowerInformation[Environment.ProcessorCount];
        var available = CallNtPowerInformation(11, IntPtr.Zero, 0, clocks,
            (uint)(Marshal.SizeOf<ProcessorPowerInformation>() * clocks.Length)) == 0;
        var rows = new List<LogicalProcessorMetric>();
        foreach (var (id, counter) in _usage.OrderBy(item => Component(item.Id, 0)).ThenBy(item => Component(item.Id, 1)))
        {
            double? usage = null;
            try { var value = counter.NextValue(); if (_primed && float.IsFinite(value)) usage = Math.Clamp(value, 0, 100); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            var parts = id.Split(',');
            double? mhz = available && parts.Length == 2 && parts[0] == "0" && int.TryParse(parts[1], out var index)
                && index >= 0 && index < clocks.Length && clocks[index].CurrentMhz > 0 ? clocks[index].CurrentMhz : null;
            rows.Add(new LogicalProcessorMetric(id, usage, mhz));
        }
        var performance = Read(_performance);
        var baseFrequency = Read(_baseFrequency);
        double? effective = _primed && performance is { } percent && baseFrequency is { } baseline ? baseline * percent / 100 : null;
        _primed = true;
        var validClocks = available ? clocks.Where(clock => clock.CurrentMhz > 0).Select(clock => (double)clock.CurrentMhz).ToArray() : [];
        return cpu with { ReportedMegahertz = validClocks.Length > 0 ? validClocks.Average() : null, EffectiveMegahertz = effective, LogicalProcessors = rows };
    }

    public void Dispose()
    {
        foreach (var item in _usage) item.Counter.Dispose();
        _performance?.Dispose();
        _baseFrequency?.Dispose();
    }

    private static int Component(string id, int position)
    {
        var parts = id.Split(',');
        return position < parts.Length && int.TryParse(parts[position], NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : int.MaxValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessorPowerInformation
    {
        public uint Number, MaxMhz, CurrentMhz, MhzLimit, MaxIdleState, CurrentIdleState;
    }

    [DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(int level, IntPtr input, uint inputSize,
        [Out] ProcessorPowerInformation[] output, uint outputSize);
}
