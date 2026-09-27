using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml.Controls;
using Skald.Core.Models;
using Windows.UI;

namespace Skald.App.Pages;

public sealed partial class NetworkPage : Page, ITelemetryPage
{
    private readonly TelemetryHistory _history = new();
    private readonly ObservableCollection<AdapterRow> _adapters = [];
    private string? _selectedId;
    public NetworkPage()
    {
        InitializeComponent();
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
        SummaryText.Text = snapshot.Network.Availability.IsSupported
            ? $"Receive {Rate(snapshot.Network.ReceiveBytesPerSecond)} · Send {Rate(snapshot.Network.SendBytesPerSecond)} · {snapshot.Network.ActiveInterfaceCount} active adapters\nTCP retransmitted segments: {(snapshot.Network.TcpRetransmitsPerSecond is { } retransmits ? $"{retransmits:F1}/s" : "Unavailable")}" 
            : snapshot.Network.Availability.Reason;
        var interfaces = snapshot.Network.Interfaces;
        _selectedId ??= interfaces.Count > 0 ? interfaces[0].Id : null;
        var pickerItems = AdapterPicker.ItemsSource as IReadOnlyList<NetworkInterfaceMetric>;
        if (pickerItems is null || !pickerItems.Select(item => item.Id).SequenceEqual(interfaces.Select(item => item.Id)))
        {
            AdapterPicker.ItemsSource = interfaces.ToArray();
            AdapterPicker.SelectedItem = interfaces.FirstOrDefault(item => item.Id == _selectedId) ?? (interfaces.Count > 0 ? interfaces[0] : null);
        }
        var rows = interfaces.Select(item => new AdapterRow(item.Name,
            $"{item.Kind} · Reported link {Link(item.LinkSpeedBitsPerSecond)} · Rx {Utilization(item.ReceiveBytesPerSecond, item.LinkSpeedBitsPerSecond)} / Tx {Utilization(item.SendBytesPerSecond, item.LinkSpeedBitsPerSecond)} of link speed",
            $"Receive {Rate(item.ReceiveBytesPerSecond)} · Send {Rate(item.SendBytesPerSecond)}",
            $"New errors: Rx {Number(item.NewReceiveErrors)}, Tx {Number(item.NewSendErrors)} · New discards: Rx {Number(item.NewReceiveDiscards)}, Tx {Number(item.NewSendDiscards)} (since previous sample)")).ToArray();
        if (_adapters.Count != rows.Length || _adapters.Where((row, index) => row.Name != rows[index].Name).Any())
        {
            _adapters.Clear();
            foreach (var row in rows) _adapters.Add(row);
        }
        else for (var index = 0; index < rows.Length; index++) _adapters[index].Set(rows[index]);
        RenderChart(snapshot.Timestamp);
    }

    private void Adapter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (AdapterPicker.SelectedItem is NetworkInterfaceMetric item) _selectedId = item.Id;
        if (_history.Samples.Count > 0) RenderChart(_history.Samples[^1].Timestamp);
    }

    private void RenderChart(DateTimeOffset now)
    {
        if (ReceiveChart is null) return;
        var series = _history.Samples.Select(sample => (sample.Timestamp,
            sample.Network.Interfaces.FirstOrDefault(item => item.Id == _selectedId) is { } adapter ? (double?)adapter.ReceiveBytesPerSecond : null)).ToArray();
        var maximum = Math.Max(1024, series.Where(item => item.Item2.HasValue).Select(item => item.Item2!.Value).DefaultIfEmpty().Max() * 1.1);
        ReceiveChart.SetTimedSeries(series, now, maximum, Color.FromArgb(255, 130, 194, 236));
        ScaleText.Text = $"0–{Rate(maximum)} · receive";
    }

    private static string Number(long? value) => value?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? "—";
    private static string Link(long bits) => bits > 0 ? $"{bits / 1000000d:F0} Mb/s" : "Unknown";
    private static string Utilization(double bytesPerSecond, long bitsPerSecond) => bitsPerSecond > 0
        ? $"{bytesPerSecond * 8 / bitsPerSecond * 100:F1}%" : "Unknown";
    private static string Rate(double bytes) => bytes >= 1048576 ? $"{bytes / 1048576:F1} MB/s" : bytes >= 1024 ? $"{bytes / 1024:F1} KB/s" : $"{bytes:F0} B/s";
    private sealed class AdapterRow(string name, string link, string traffic, string errors) : INotifyPropertyChanged
    {
        public string Name { get; } = name;
        public string Link { get; private set; } = link;
        public string Traffic { get; private set; } = traffic;
        public string Errors { get; private set; } = errors;
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Set(AdapterRow row)
        {
            if (Link != row.Link) { Link = row.Link; PropertyChanged?.Invoke(this, new(nameof(Link))); }
            if (Traffic != row.Traffic) { Traffic = row.Traffic; PropertyChanged?.Invoke(this, new(nameof(Traffic))); }
            if (Errors != row.Errors) { Errors = row.Errors; PropertyChanged?.Invoke(this, new(nameof(Errors))); }
        }
    }
}
