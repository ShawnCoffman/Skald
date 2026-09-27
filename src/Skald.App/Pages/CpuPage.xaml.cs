using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Skald.Core.Models;
using Windows.UI;
namespace Skald.App.Pages;
public sealed partial class CpuPage : Page, ITelemetryPage
{
    private readonly TelemetryHistory _history = new();
    private readonly ObservableCollection<CoreRow> _cores = [];
    public event Action? ProcessesRequested;
    private void Processes_Click(object sender, RoutedEventArgs e) => ProcessesRequested?.Invoke();
    public CpuPage()
    {
        InitializeComponent();
        CoreList.ItemsSource = _cores;
    }
    public void ShowHistory(IReadOnlyList<SystemMetricsSnapshot> samples)
    {
        _history.Replace(samples);
        if (samples.Count > 0) Update(samples[^1], []);
    }
    public void Update(SystemMetricsSnapshot snapshot, IReadOnlyList<DiagnosticFinding> findings)
    {
        _history.Add(snapshot);
        var validCpu = _history.Samples.Where(sample => sample.Cpu.Availability.IsSupported).Select(sample => sample.Cpu.UtilizationPercent).ToArray();
        StatusText.Text = $"Total {snapshot.Cpu.UtilizationPercent:F1}% · 3-minute average {validCpu.DefaultIfEmpty().Average():F1}% / peak {validCpu.DefaultIfEmpty().Max():F1}% · OS reported clock {(snapshot.Cpu.ReportedMegahertz is { } megahertz ? $"{megahertz / 1000:F2} GHz" : "unavailable")}";
        var busiest = snapshot.Cpu.LogicalProcessors.Where(core => core.UtilizationPercent.HasValue).OrderByDescending(core => core.UtilizationPercent).FirstOrDefault();
        PressureText.Text = $"Busiest logical processor: {(busiest is null ? "Unavailable" : $"{busiest.Id} · {busiest.UtilizationPercent:F1}%")} · Queue: {Format(snapshot.Cpu.ProcessorQueueLength)}\nInterrupt time: {Format(snapshot.Cpu.InterruptPercent, "%")} · DPC time: {Format(snapshot.Cpu.DpcPercent, "%")}";
        TopProcessesText.Text = string.Join("\n", snapshot.Processes.Where(process => process.CpuAvailable).OrderByDescending(process => process.CpuPercent).Take(5).Select(process => $"{process.Name} (PID {process.ProcessId}) · {process.CpuPercent:F1}%"));
        CpuChart.SetTimedSeries(_history.Samples.Select(s => (s.Timestamp, s.Cpu.Availability.IsSupported ? (double?)s.Cpu.UtilizationPercent : null)), snapshot.Timestamp, 100, Color.FromArgb(255,95,222,185));
        var cores = snapshot.Cpu.LogicalProcessors.Select(core => new CoreRow($"Processor {core.Id}", core.UtilizationPercent is { } usage ? $"{usage:F1}%" : "Unavailable", core.ReportedMegahertz is { } clock ? $"{clock / 1000:F2} GHz reported" : "Clock unavailable")).ToArray();
        if (_cores.Count != cores.Length || _cores.Where((row, index) => row.Name != cores[index].Name).Any())
        {
            _cores.Clear();
            foreach (var core in cores) _cores.Add(core);
        }
        else for (var index = 0; index < cores.Length; index++) _cores[index].Set(cores[index].Usage, cores[index].Clock);
    }
    private sealed class CoreRow(string name, string usage, string clock) : INotifyPropertyChanged
    {
        public string Name { get; } = name;
        public string Usage { get; private set; } = usage;
        public string Clock { get; private set; } = clock;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Set(string usage, string clock)
        {
            if (Usage != usage) { Usage = usage; PropertyChanged?.Invoke(this, new(nameof(Usage))); }
            if (Clock != clock) { Clock = clock; PropertyChanged?.Invoke(this, new(nameof(Clock))); }
        }
    }
    private static string Format(double? value, string suffix = "") => value is { } number ? $"{number:F1}{suffix}" : "Unavailable";
}
