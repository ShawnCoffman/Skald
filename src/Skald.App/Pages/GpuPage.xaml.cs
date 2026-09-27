using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Skald.Core.Models;
using Windows.UI;

namespace Skald.App.Pages;

public sealed partial class GpuPage : Page, ITelemetryPage
{
    private readonly TelemetryHistory _history = new();
    private readonly ObservableCollection<DeviceRow> _devices = [];
    private readonly ObservableCollection<AdapterRow> _adapters = [];
    public event Action? ProcessesRequested;
    private void Processes_Click(object sender, RoutedEventArgs e) => ProcessesRequested?.Invoke();
    public GpuPage()
    {
        InitializeComponent();
        DeviceList.ItemsSource = _devices;
        AdapterList.ItemsSource = _adapters;
    }

    public void ShowHistory(IReadOnlyList<SystemMetricsSnapshot> samples)
    {
        _history.Replace(samples);
        if (samples.Count > 0) Update(samples[^1], []);
    }

    public void Update(SystemMetricsSnapshot snapshot, IReadOnlyList<DiagnosticFinding> findings)
    {
        _history.Add(snapshot);
        EngineStatus.Text = snapshot.GpuEnginesAvailable ? $"{snapshot.GpuEngines.Count} Windows engine counters · busiest engine {snapshot.GpuEngines.Select(engine => engine.UtilizationPercent).DefaultIfEmpty(0).Max():F1}%" : "Windows GPU engine counters unavailable.";
        EngineChart.SetTimedSeries(_history.Samples.Select(sample => (sample.Timestamp, sample.GpuEnginesAvailable ? (double?)sample.GpuEngines.Select(engine => engine.UtilizationPercent).DefaultIfEmpty(0).Max() : null)), snapshot.Timestamp, 100, Color.FromArgb(255, 102, 202, 232));
        EngineDetails.Text = snapshot.GpuEngines.Count == 0 ? "No active engine reported." : string.Join("\n", snapshot.GpuEngines.Take(8).Select(engine => $"{engine.EngineType} · {engine.UtilizationPercent:F1}% · {engine.EngineKey}"));
        TopProcessesText.Text = string.Join("\n", snapshot.Processes.Where(process => process.GpuPercent is > 0).OrderByDescending(process => process.GpuPercent).Take(5).Select(process => $"{process.Name} (PID {process.ProcessId}) · busiest engine {process.GpuPercent:F1}%"));
        var devices = snapshot.GpuDevices.Select(device => new DeviceRow(
            device.Name,
            device.Source,
            device.TemperatureCelsius is { } temperature ? $"{temperature:F0} °C" : "Unavailable",
            device.PowerWatts is { } watts ? $"{watts:F1} W" : "Unavailable",
            device.DedicatedUsedMegabytes is { } used && device.DedicatedTotalMegabytes is { } total
                ? $"{used / 1024d:F1} / {total / 1024d:F1} GB"
                : "Unavailable")).ToArray();
        if (_devices.Count != devices.Length || _devices.Where((row, index) => row.Name != devices[index].Name || row.Source != devices[index].Source).Any())
        {
            _devices.Clear();
            foreach (var device in devices) _devices.Add(device);
        }
        else for (var index = 0; index < devices.Length; index++) _devices[index].Set(devices[index]);
        SensorStatus.Text = devices.Length == 0
            ? "No supported vendor sensors found. NVIDIA temperature and power need a compatible NVML driver; other vendors are not connected yet."
            : $"{devices.Length} device{(devices.Length == 1 ? string.Empty : "s")} reporting";

        var adapters = snapshot.GpuAdapters.Select((adapter, index) => new AdapterRow(
            $"GPU adapter {index + 1}",
            adapter.AdapterKey,
            FormatMemory(adapter.DedicatedUsedMegabytes),
            FormatMemory(adapter.SharedUsedMegabytes))).ToArray();
        if (_adapters.Count != adapters.Length || _adapters.Where((row, index) => row.Key != adapters[index].Key).Any())
        {
            _adapters.Clear();
            foreach (var adapter in adapters) _adapters.Add(adapter);
        }
        else for (var index = 0; index < adapters.Length; index++) _adapters[index].Set(adapters[index]);
        MemoryStatus.Text = adapters.Length == 0
            ? "Windows GPU Adapter Memory counters are unavailable."
            : $"{adapters.Length} adapter{(adapters.Length == 1 ? string.Empty : "s")} · live usage, not memory capacity";
    }

    private static string FormatMemory(double? megabytes)
        => megabytes is { } value ? $"{value / 1024d:F1} GB" : "Unavailable";

    private sealed class DeviceRow(string name, string source, string temperature, string power, string memory) : INotifyPropertyChanged
    {
        public string Name { get; } = name;
        public string Source { get; } = source;
        public string Temperature { get; private set; } = temperature;
        public string Power { get; private set; } = power;
        public string Memory { get; private set; } = memory;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Set(DeviceRow row)
        {
            if (Temperature != row.Temperature) { Temperature = row.Temperature; PropertyChanged?.Invoke(this, new(nameof(Temperature))); }
            if (Power != row.Power) { Power = row.Power; PropertyChanged?.Invoke(this, new(nameof(Power))); }
            if (Memory != row.Memory) { Memory = row.Memory; PropertyChanged?.Invoke(this, new(nameof(Memory))); }
        }
    }
    private sealed class AdapterRow(string name, string key, string dedicated, string shared) : INotifyPropertyChanged
    {
        public string Name { get; } = name;
        public string Key { get; } = key;
        public string Dedicated { get; private set; } = dedicated;
        public string Shared { get; private set; } = shared;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Set(AdapterRow row)
        {
            if (Dedicated != row.Dedicated) { Dedicated = row.Dedicated; PropertyChanged?.Invoke(this, new(nameof(Dedicated))); }
            if (Shared != row.Shared) { Shared = row.Shared; PropertyChanged?.Invoke(this, new(nameof(Shared))); }
        }
    }
}
