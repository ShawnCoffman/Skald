using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Skald.Core.Models;
using Windows.UI;

namespace Skald.App.Pages;

public sealed partial class PerformancePage : Page, ITelemetryPage
{
    private readonly TelemetryHistory _history = new();
    private readonly List<double?> _cpuHistory = [];
    private readonly List<double?> _memoryHistory = [];
    private readonly List<double?> _diskHistory = [];
    private readonly List<double?> _networkHistory = [];

    public PerformancePage() => InitializeComponent();


    public void ShowHistory(IReadOnlyList<SystemMetricsSnapshot> samples)
    {
        _history.Replace(samples);
        if (samples.Count > 0) Update(samples[^1], []);
    }

    public void Update(SystemMetricsSnapshot snapshot, IReadOnlyList<DiagnosticFinding> findings)
    {
        _history.Add(snapshot);
        CpuText.Text = snapshot.Cpu.Availability.IsSupported ? $"{snapshot.Cpu.UtilizationPercent:F1}%" : "Unavailable";
        CpuDetail.Text = snapshot.Cpu.Availability.IsSupported ? $"{snapshot.Cpu.LogicalProcessorCount} logical processors" : snapshot.Cpu.Availability.Reason;
        MemoryText.Text = snapshot.Memory.Availability.IsSupported ? $"{snapshot.Memory.UsedPercent:F1}%" : "Unavailable";
        MemoryDetail.Text = snapshot.Memory.Availability.IsSupported ? $"{FormatBytes(snapshot.Memory.AvailableBytes)} available" : snapshot.Memory.Availability.Reason;
        DiskText.Text = snapshot.Disk.Availability.IsSupported ? $"{snapshot.Disk.ActiveTimePercent:F1}%" : "Unavailable";
        DiskDetail.Text = snapshot.Disk.Availability.IsSupported ? $"{snapshot.Disk.TransfersPerSecond:F1} transfers/sec" : snapshot.Disk.Availability.Reason;
        NetworkText.Text = snapshot.Network.Availability.IsSupported ? FormatRate(snapshot.Network.ReceiveBytesPerSecond) : "Unavailable";
        NetworkDetail.Text = snapshot.Network.Availability.IsSupported ? $"{snapshot.Network.ActiveInterfaceCount} active interfaces" : snapshot.Network.Availability.Reason;

        _cpuHistory.Clear(); _memoryHistory.Clear(); _diskHistory.Clear(); _networkHistory.Clear();
        foreach (var sample in _history.Samples)
        {
            _cpuHistory.Add(sample.Cpu.Availability.IsSupported ? sample.Cpu.UtilizationPercent : null);
            _memoryHistory.Add(sample.Memory.Availability.IsSupported ? sample.Memory.UsedPercent : null);
            _diskHistory.Add(sample.Disk.Availability.IsSupported ? sample.Disk.ActiveTimePercent : null);
            _networkHistory.Add(sample.Network.Availability.IsSupported ? sample.Network.ReceiveBytesPerSecond : null);
        }

        CpuChart.SetTimedSeries(_history.Samples.Select((sample, index) => (sample.Timestamp, _cpuHistory[index])), snapshot.Timestamp, 100, Color.FromArgb(255, 95, 222, 185));
        MemoryChart.SetTimedSeries(_history.Samples.Select((sample, index) => (sample.Timestamp, _memoryHistory[index])), snapshot.Timestamp, 100, Color.FromArgb(255, 173, 150, 238));
        DiskChart.SetTimedSeries(_history.Samples.Select((sample, index) => (sample.Timestamp, _diskHistory[index])), snapshot.Timestamp, 100, Color.FromArgb(255, 237, 180, 101));
        var networkMaximum = Math.Max(1024, _networkHistory.Where(value => value.HasValue).Select(value => value!.Value).DefaultIfEmpty().Max() * 1.1);
        NetworkChart.SetTimedSeries(_history.Samples.Select((sample, index) => (sample.Timestamp, _networkHistory[index])), snapshot.Timestamp, networkMaximum, Color.FromArgb(255, 130, 194, 236));
        NetworkScale.Text = $"0–{FormatRate(networkMaximum)}";
    }

    private static string FormatRate(double bytesPerSecond)
    {
        var value = bytesPerSecond;
        var suffix = "B/s";
        if (value >= 1024) { value /= 1024; suffix = "KB/s"; }
        if (value >= 1024) { value /= 1024; suffix = "MB/s"; }
        return $"{value:F1} {suffix}";
    }

    private static string FormatBytes(ulong bytes)
    {
        var value = (double)bytes;
        var suffix = "B";
        if (value >= 1024) { value /= 1024; suffix = "KB"; }
        if (value >= 1024) { value /= 1024; suffix = "MB"; }
        if (value >= 1024) { value /= 1024; suffix = "GB"; }
        return $"{value:F1} {suffix}";
    }
}
