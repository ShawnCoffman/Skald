using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Skald.Core.Models;
using Windows.UI;
namespace Skald.App.Pages;
public sealed partial class MemoryPage : Page, ITelemetryPage
{
    private readonly TelemetryHistory _history = new();
    public event Action? ProcessesRequested;
    private void Processes_Click(object sender, RoutedEventArgs e) => ProcessesRequested?.Invoke();
    public MemoryPage() => InitializeComponent();
    public void ShowHistory(IReadOnlyList<SystemMetricsSnapshot> samples)
    {
        _history.Replace(samples);
        if (samples.Count > 0) Update(samples[^1], []);
    }
    public void Update(SystemMetricsSnapshot snapshot, IReadOnlyList<DiagnosticFinding> findings)
    {
        _history.Add(snapshot);
        UsageText.Text = snapshot.Memory.Availability.IsSupported ? $"{snapshot.Memory.UsedPercent:F1}% physical memory used" : "Unavailable";
        DetailText.Text = $"{snapshot.Memory.UsedBytes / 1073741824d:F1} GB used · {snapshot.Memory.AvailableBytes / 1073741824d:F1} GB available · {snapshot.Memory.TotalBytes / 1073741824d:F1} GB total";
        PressureText.Text = $"Committed: {(snapshot.Memory.CommitBytes is { } commit ? $"{commit / 1073741824d:F1}" : "Unavailable")} / {(snapshot.Memory.CommitLimitBytes is { } limit ? $"{limit / 1073741824d:F1}" : "Unavailable")} GB · Page reads: {(snapshot.Memory.PageReadsPerSecond is { } reads ? $"{reads:F1}/s" : "Unavailable")}";
        TopProcessesText.Text = string.Join("\n", snapshot.Processes.Where(process => process.PrivateMemoryAvailable).OrderByDescending(process => process.PrivateMemoryMegabytes).Take(5).Select(process => $"{process.Name} (PID {process.ProcessId}) · {process.PrivateMemoryMegabytes:F0} MB private · {(process.WorkingSetAvailable ? $"{process.WorkingSetMegabytes:F0} MB working set" : "working set unavailable")}"));
        MemoryChart.SetTimedSeries(_history.Samples.Select(s => (s.Timestamp, s.Memory.Availability.IsSupported ? (double?)s.Memory.UsedPercent : null)), snapshot.Timestamp, 100, Color.FromArgb(255,173,150,238));
        AvailableChart.SetTimedSeries(_history.Samples.Select(s => (s.Timestamp, s.Memory.Availability.IsSupported ? (double?)(s.Memory.AvailableBytes / 1073741824d) : null)), snapshot.Timestamp, Math.Max(1, snapshot.Memory.TotalBytes / 1073741824d), Color.FromArgb(255,110,204,224));
    }
}
