using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Skald.Core.Models;
using Skald.Collectors;
using Skald.Triage;
using Windows.UI;

namespace Skald.App.Pages;

public sealed partial class DisksPage : Page, ITelemetryPage
{
    private readonly TelemetryHistory _history = new();
    private readonly ObservableCollection<DiskRow> _disks = [];
    public event Action? ProcessesRequested;
    private void Processes_Click(object sender, RoutedEventArgs e) => ProcessesRequested?.Invoke();
    private string? _selectedDisk;
    public DisksPage()
    {
        InitializeComponent();
        DiskList.ItemsSource = _disks;
    }

    public void ShowHistory(IReadOnlyList<SystemMetricsSnapshot> samples)
    {
        _history.Replace(samples);
        if (samples.Count > 0) Update(samples[^1], []);
    }

    public void Update(SystemMetricsSnapshot snapshot, IReadOnlyList<DiagnosticFinding> findings)
    {
        _history.Add(snapshot);
        ActivityText.Text = snapshot.Disk.Availability.IsSupported ? $"{snapshot.Disk.ActiveTimePercent:F1}% aggregate active" : "Aggregate unavailable";
        DetailText.Text = snapshot.Disk.Availability.IsSupported ? $"{snapshot.Disk.TransfersPerSecond:F1} transfers/sec · {snapshot.Disk.PhysicalDisks.Count} physical disk instances" : snapshot.Disk.Availability.Reason;
        var rows = snapshot.Disk.PhysicalDisks.Select(disk => new DiskRow(disk.Name,
            $"Active {Format(disk.ActivePercent, "%")} · Queue {Format(disk.QueueLength)}",
            $"Read {Rate(disk.ReadBytesPerSecond)} ({Format(disk.ReadsPerSecond, "/s")}) · Write {Rate(disk.WriteBytesPerSecond)} ({Format(disk.WritesPerSecond, "/s")})",
            $"Read latency {Format(disk.ReadLatencyMilliseconds, " ms")} · Write latency {Format(disk.WriteLatencyMilliseconds, " ms")}")).ToArray();
        if (_disks.Count != rows.Length || _disks.Where((row, index) => row.Name != rows[index].Name).Any())
        {
            _disks.Clear();
            foreach (var row in rows) _disks.Add(row);
        }
        else for (var index = 0; index < rows.Length; index++) _disks[index].Set(rows[index]);
        var disks = snapshot.Disk.PhysicalDisks;
        _selectedDisk ??= disks.Count > 0 ? disks[0].Name : null;
        var pickerItems = DiskPicker.ItemsSource as IReadOnlyList<PhysicalDiskMetric>;
        if (pickerItems is null || !pickerItems.Select(item => item.Name).SequenceEqual(disks.Select(item => item.Name)))
        {
            DiskPicker.ItemsSource = disks.ToArray();
            DiskPicker.SelectedItem = disks.FirstOrDefault(disk => disk.Name == _selectedDisk) ?? (disks.Count > 0 ? disks[0] : null);
        }
        TopProcessesText.Text = string.Join("\n", snapshot.Processes.Where(process => process.ReadBytesPerSecond.HasValue || process.WriteBytesPerSecond.HasValue)
            .OrderByDescending(process => (process.ReadBytesPerSecond ?? 0) + (process.WriteBytesPerSecond ?? 0)).Take(5)
            .Select(process => $"{process.Name} (PID {process.ProcessId}) · read {Rate(process.ReadBytesPerSecond)} · write {Rate(process.WriteBytesPerSecond)}"));
        var maximum = Math.Max(1, _history.Samples.Select(sample => sample.Disk.TransfersPerSecond).DefaultIfEmpty().Max() * 1.1);
        TransfersChart.SetTimedSeries(_history.Samples.Select(sample => (sample.Timestamp, sample.Disk.Availability.IsSupported ? (double?)sample.Disk.TransfersPerSecond : null)), snapshot.Timestamp, maximum, Color.FromArgb(255, 237, 180, 101));
        RenderDiskChart(snapshot.Timestamp);
    }

    private void Disk_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (DiskPicker.SelectedItem is PhysicalDiskMetric disk) _selectedDisk = disk.Name;
        if (_history.Samples.Count > 0) RenderDiskChart(_history.Samples[^1].Timestamp);
    }

    private void RenderDiskChart(DateTimeOffset now)
    {
        if (DiskChart is null) return;
        var series = _history.Samples.Select(sample => (sample.Timestamp,
            sample.Disk.PhysicalDisks.FirstOrDefault(disk => disk.Name == _selectedDisk) is { } disk
                && disk.ReadBytesPerSecond is { } read && disk.WriteBytesPerSecond is { } write ? (double?)(read + write) : null)).ToArray();
        var maximum = Math.Max(1024, series.Where(item => item.Item2.HasValue).Select(item => item.Item2!.Value).DefaultIfEmpty().Max() * 1.1);
        DiskChart.SetTimedSeries(series, now, maximum, Color.FromArgb(255, 237, 180, 101));
        DiskScaleText.Text = $"0–{Rate(maximum)} · read + write";
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        StorageHealthText.Text = await StorageHealthCollector.CollectAsync();
        CapacityText.Text = await Task.Run(() =>
        {
            try
            {
                return string.Join("\n", DriveInfo.GetDrives().Where(drive => drive.DriveType == DriveType.Fixed && drive.IsReady)
                    .Select(drive => $"{drive.Name} · {drive.AvailableFreeSpace / 1073741824d:F1} GB free / {drive.TotalSize / 1073741824d:F1} GB"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return $"Capacity unavailable ({ex.GetType().Name})."; }
        });
    }

    private static string Format(double? value, string suffix = "") => value is { } number ? $"{number:F1}{suffix}" : "Unavailable";
    private static string Rate(double? bytes) => bytes is { } value ? value >= 1048576 ? $"{value / 1048576:F1} MB/s" : $"{value / 1024:F1} KB/s" : "Unavailable";
    private sealed class DiskRow(string name, string activity, string throughput, string latency) : INotifyPropertyChanged
    {
        public string Name { get; } = name;
        public string Activity { get; private set; } = activity;
        public string Throughput { get; private set; } = throughput;
        public string Latency { get; private set; } = latency;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Set(DiskRow row)
        {
            if (Activity != row.Activity) { Activity = row.Activity; PropertyChanged?.Invoke(this, new(nameof(Activity))); }
            if (Throughput != row.Throughput) { Throughput = row.Throughput; PropertyChanged?.Invoke(this, new(nameof(Throughput))); }
            if (Latency != row.Latency) { Latency = row.Latency; PropertyChanged?.Invoke(this, new(nameof(Latency))); }
        }
    }
}
