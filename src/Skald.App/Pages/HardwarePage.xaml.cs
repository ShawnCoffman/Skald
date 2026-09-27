using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Skald.App.Controls;
using Skald.Collectors;
using Skald.Core.Models;

namespace Skald.App.Pages;

public sealed partial class HardwarePage : Page, ITelemetryPage
{
    private HardwareInventory? _inventory;
    private SystemMetricsSnapshot? _latestSnapshot;
    private IReadOnlyList<SystemMetricsSnapshot> _recentHistory = [];
    private bool _loading;

    public HardwarePage() => InitializeComponent();

    public void Update(SystemMetricsSnapshot snapshot, IReadOnlyList<DiagnosticFinding> findings)
    {
        _latestSnapshot = snapshot;
        RenderLive();
        RenderUnmapped();
    }

    public void ShowHistory(IReadOnlyList<SystemMetricsSnapshot> history)
    {
        _recentHistory = history;
        if (history.Count > 0) _latestSnapshot = history[^1];
        RenderLive();
        RenderTimeline();
        RenderUnmapped();
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_inventory is null || DateTimeOffset.Now - _inventory.CollectedAt > TimeSpan.FromMinutes(5))
            await RefreshAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (_loading) return;
        _loading = true;
        RefreshButton.IsEnabled = false;
        StatusText.Text = "Reading hardware identities and health sources in the background…";
        try
        {
            _inventory = await HardwareInventoryCollector.CollectAsync();
            using var scroll = ScrollPositionKeeper.Capture(this);
            CoverageText.Text = string.Join("\n", _inventory.SourceStatus);
            StatusText.Text = $"Checked {_inventory.CollectedAt:g} · {_inventory.Devices.Count} devices · " +
                $"{_inventory.Devices.Count(device => device.Health is HardwareHealthState.Attention or HardwareHealthState.ReportedProblem)} needing attention";
            RenderDevices();
            RenderUnmapped();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Hardware refresh failed: {ex.Message}";
        }
        finally
        {
            _loading = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private void Category_Changed(object sender, SelectionChangedEventArgs e) => RenderDevices();

    private void RenderDevices()
    {
        if (_inventory is null || DeviceList is null || CategoryPicker is null) return;
        using var scroll = ScrollPositionKeeper.Capture(this);
        var category = (CategoryPicker.SelectedItem as ComboBoxItem)?.Content?.ToString();
        var selected = (DeviceList.SelectedItem as HardwareDevice)?.Key;
        var devices = _inventory.Devices.Where(device => category is null or "All devices" || device.Category == category).ToArray();
        DeviceList.ItemsSource = devices;
        DeviceList.SelectedItem = devices.FirstOrDefault(device => device.Key == selected) ?? devices.FirstOrDefault();
        if (devices.Length == 0) ClearSelection();
    }

    private void Device_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (DeviceList.SelectedItem is not HardwareDevice device) { ClearSelection(); return; }
        DeviceHeading.Text = device.Name;
        HealthText.Text = device.HealthLabel + " · " + device.Category;
        IdentityText.Text = $"Identity: {device.Identity} · Match: {device.IdentityConfidence}";
        FactsText.Text = device.Facts.Count == 0 ? "No identity facts were exposed." : string.Join("\n\n",
            device.Facts.Select(fact => $"{fact.Label}: {fact.Value}\n{fact.Source} · {fact.Confidence} match"));
        EvidenceText.Text = device.Signals.Count == 0
            ? "No component-specific health evidence was exposed. Unknown is not healthy."
            : string.Join("\n\n", device.Signals.Select(signal =>
                $"{signal.Title} · {signal.State}\n{signal.Detail}\n{signal.Source}" +
                (signal.ObservedAt is { } at ? $" · {at.ToLocalTime():g}" : string.Empty)));
        RenderLive();
        RenderTimeline();
    }

    private void ClearSelection()
    {
        DeviceHeading.Text = "No device in this category";
        HealthText.Text = "Unknown";
        IdentityText.Text = FactsText.Text = EvidenceText.Text = LiveText.Text = TimelineText.Text = string.Empty;
    }

    private void RenderLive()
    {
        if (DeviceList?.SelectedItem is not HardwareDevice device || LiveText is null) return;
        if (_latestSnapshot is not { } snapshot)
        {
            LiveText.Text = "Waiting for a telemetry sample.";
            return;
        }
        if (device.Category == "Storage" && device.Facts.FirstOrDefault(fact => fact.Label == "Windows disk number") is { } numberFact
            && uint.TryParse(numberFact.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            var matches = snapshot.Disk.PhysicalDisks.Where(disk => HardwareIdentity.MatchesDiskCounter(number, disk.Name)).ToArray();
            LiveText.Text = matches.Length == 1
                ? $"Probable PhysicalDisk counter match: {matches[0].Name}\nActive {Format(matches[0].ActivePercent, "%")} · read {Rate(matches[0].ReadBytesPerSecond)} · write {Rate(matches[0].WriteBytesPerSecond)}\nRead latency {Format(matches[0].ReadLatencyMilliseconds, " ms")} · write latency {Format(matches[0].WriteLatencyMilliseconds, " ms")}."
                : "PhysicalDisk counter was not uniquely matched to this Windows disk.";
            return;
        }
        if (device.Category == "Network")
        {
            var id = device.Identity.Trim('{', '}');
            var match = snapshot.Network.Interfaces.FirstOrDefault(item => item.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            LiveText.Text = match is null ? "No active interface counter matched this interface GUID."
                : $"Verified interface GUID match: {match.Name}\nReceive {Rate(match.ReceiveBytesPerSecond)} · send {Rate(match.SendBytesPerSecond)}.";
            return;
        }
        LiveText.Text = device.Category switch
        {
            "Processor" when _inventory?.Devices.Count(item => item.Category == "Processor") == 1 =>
                $"System CPU utilization: {snapshot.Cpu.UtilizationPercent:F1}%. Aggregate reading for the only reported processor socket.",
            "Battery" => "System battery discharge is aggregate, not assigned to this individual pack.",
            "Graphics" => "Windows GPU counters and vendor telemetry are not yet verified against this PnP adapter.",
            "Memory" => "System memory use is aggregate; it cannot identify a failing module.",
            _ => "No live reading is safely attributable to this device."
        };
    }

    private void RenderUnmapped()
    {
        if (UnmappedText is null) return;
        var lines = new List<string>();
        if (_inventory is { } inventory)
            lines.AddRange(inventory.UnmappedSignals.Take(10).Select(signal =>
                $"{signal.ObservedAt?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "Time unavailable"} · {signal.Title}\n{signal.Detail}"));
        if (_latestSnapshot is { } snapshot)
            lines.AddRange(snapshot.GpuDevices.Select(device =>
                $"Vendor GPU sensor (not mapped to a Windows adapter): {device.Name} · {device.DeviceId ?? "ID unavailable"}"));
        UnmappedText.Text = lines.Count == 0 ? "No unmapped hardware reports or vendor GPU sensors in available sources." : string.Join("\n\n", lines);
    }

    private void RenderTimeline()
    {
        if (DeviceList?.SelectedItem is not HardwareDevice device || TimelineText is null) return;
        if (device.Category != "Storage" || device.Facts.FirstOrDefault(fact => fact.Label == "Windows disk number") is not { } numberFact
            || !uint.TryParse(numberFact.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number))
        {
            TimelineText.Text = "A device-specific incident timeline is currently available for Windows disks.";
            return;
        }

        var lines = new List<string>();
        var readings = _recentHistory.Select(sample => new
        {
            sample.Timestamp,
            Matches = sample.Disk.PhysicalDisks.Where(disk => HardwareIdentity.MatchesDiskCounter(number, disk.Name)).ToArray()
        }).Where(item => item.Matches.Length == 1).Select(item => (item.Timestamp, Disk: item.Matches[0])).ToArray();
        if (readings.Length > 0)
        {
            var readPeak = readings.Max(item => item.Disk.ReadLatencyMilliseconds ?? 0);
            var writePeak = readings.Max(item => item.Disk.WriteLatencyMilliseconds ?? 0);
            lines.Add($"Recent counter window · {readings.Length} probable disk-number matches · peak read {readPeak:F1} ms, peak write {writePeak:F1} ms. Counter samples are available only while Skald runs.");
            var high = readings.Where(item => (item.Disk.ReadsPerSecond > 0 && item.Disk.ReadLatencyMilliseconds >= 100)
                || (item.Disk.WritesPerSecond > 0 && item.Disk.WriteLatencyMilliseconds >= 100)).TakeLast(8);
            lines.AddRange(high.Select(item => $"{item.Timestamp.ToLocalTime():g} · sampled latency ≥100 ms · read {Format(item.Disk.ReadLatencyMilliseconds, " ms")}, write {Format(item.Disk.WriteLatencyMilliseconds, " ms")}"));
            var overlap = device.Signals.Count(signal => signal.ObservedAt is { } at && signal.Source.Contains("event", StringComparison.OrdinalIgnoreCase)
                && at >= readings[0].Timestamp.AddMinutes(-2) && at <= readings[^1].Timestamp.AddMinutes(2));
            if (overlap > 0) lines.Add($"{overlap} storage event(s) near the sampled window (±2 min). Timing alone does not establish a cause.");
        }
        else lines.Add("No uniquely matched PhysicalDisk samples in the current rolling window.");

        lines.AddRange(device.Signals.Where(signal => signal.ObservedAt.HasValue)
            .OrderByDescending(signal => signal.ObservedAt).Take(15)
            .Select(signal => $"{signal.ObservedAt!.Value.ToLocalTime():g} · {signal.Title}\n{signal.Detail}\n{signal.Source}"));
        TimelineText.Text = string.Join("\n\n", lines);
    }

    private static string Format(double? value, string suffix) => value is { } number ? $"{number:F1}{suffix}" : "Unavailable";
    private static string Rate(double? bytes) => bytes is { } value ? value >= 1048576 ? $"{value / 1048576:F1} MiB/s" : $"{value / 1024:F1} KiB/s" : "Unavailable";
}
