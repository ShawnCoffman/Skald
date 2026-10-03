using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Skald.App.Controls;
using Skald.Collectors;
using Skald.Triage;
using Skald.Core.Models;
using Windows.ApplicationModel.DataTransfer;

namespace Skald.App.Pages;

public sealed partial class SummaryPage : Page, ITelemetryPage
{
    private SystemInventory? _inventory;
    private bool _loading;
    private bool _healthLoading;
    private DateTimeOffset _healthChecked;
    public event Action<string>? NavigateRequested;

    public SummaryPage() => InitializeComponent();

    public void Update(SystemMetricsSnapshot snapshot, IReadOnlyList<DiagnosticFinding> findings)
    {
        CpuValue.Text = snapshot.Cpu.Availability.IsSupported ? $"{snapshot.Cpu.UtilizationPercent:F1}%" : "Unavailable";
        CpuState.Text = $"{snapshot.Cpu.LogicalProcessorCount} logical processors";
        MemoryValue.Text = snapshot.Memory.Availability.IsSupported ? $"{snapshot.Memory.UsedPercent:F1}%" : "Unavailable";
        MemoryState.Text = snapshot.Memory.Availability.IsSupported ? $"{Gigabytes(snapshot.Memory.UsedBytes)} / {Gigabytes(snapshot.Memory.TotalBytes)}" : snapshot.Memory.Availability.Reason;
        DiskValue.Text = snapshot.Disk.Availability.IsSupported ? $"{snapshot.Disk.ActiveTimePercent:F1}%" : "Unavailable";
        DiskState.Text = snapshot.Disk.Availability.IsSupported ? $"{snapshot.Disk.TransfersPerSecond:F0} transfers/sec" : snapshot.Disk.Availability.Reason;
        NetworkValue.Text = snapshot.Network.Availability.IsSupported ? Rate(snapshot.Network.ReceiveBytesPerSecond) : "Unavailable";
        NetworkState.Text = snapshot.Network.Availability.IsSupported ? $"Send {Rate(snapshot.Network.SendBytesPerSecond)}" : snapshot.Network.Availability.Reason;
        var uptime = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var power = snapshot.Power.Availability.IsSupported ? snapshot.Power.Source.ToString() : "Power unknown";
        LiveContext.Text = $"Up {uptime.Days}d {uptime.Hours}h {uptime.Minutes}m · {snapshot.Processes.Count} processes · {power}";
    }

    public void ShowHistory(IReadOnlyList<SystemMetricsSnapshot> samples, IReadOnlyList<DiagnosticFinding> findings)
    {
        if (samples.Count > 0) Update(samples[^1], findings);
    }

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        if (_inventory is null || DateTimeOffset.Now - _inventory.CollectedAt > TimeSpan.FromMinutes(5)) await RefreshAsync();
        if (_healthChecked == default || DateTimeOffset.Now - _healthChecked > TimeSpan.FromMinutes(5)) await RefreshHealthAsync();
    }
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await RefreshAsync();
        await RefreshHealthAsync();
    }

    private void Reliability_Click(object sender, RoutedEventArgs e) => NavigateRequested?.Invoke("reliability");
    private void Hardware_Click(object sender, RoutedEventArgs e) => NavigateRequested?.Invoke("hardware");
    private void Updates_Click(object sender, RoutedEventArgs e) => NavigateRequested?.Invoke("updates");

    private async Task RefreshHealthAsync()
    {
        if (_healthLoading) return;
        _healthLoading = true;
        HealthStatusText.Text = "Checking reports, dumps, and updates…";
        try
        {
            var reliabilityTask = WindowsReliabilityCollector.CollectAsync();
            var dumpsTask = CrashDumpCollector.CollectAsync();
            var updatesTask = WindowsUpdateCollector.CollectAsync();
            var reliability = await reliabilityTask;
            var dumps = await dumpsTask;
            var updates = await updatesTask;
            using var scroll = ScrollPositionKeeper.Capture(this);
            var summary = ReliabilitySummary.From(reliability, TimeSpan.FromDays(7));
            ReliabilitySummaryText.Text = $"{summary.AppCrashes} app crashes · {summary.AppHangs} hangs\n{summary.UnexpectedShutdowns} unexpected shutdowns · {summary.BugChecks} bug checks\n{summary.HardwareReports} hardware reports · {dumps.Files.Count} dump files available";
            LatestProblemText.Text = summary.LatestProblem is { } latest ? $"Latest: {latest.DisplayTime} · {latest.Component} · {latest.Summary}" : "No error or critical report in available history.";
            var failed = updates.Entries.Count(item => item.Result is "Failed" or "Aborted" or "Succeeded with errors");
            UpdateSummaryText.Text = $"Pending in local catalog: {updates.PendingCount?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? "Unknown"}\nFailed recent history entries: {failed}\nLast successful check: {updates.LastSuccessfulSearch?.ToString("g", System.Globalization.CultureInfo.CurrentCulture) ?? "Unknown"}";
            HealthStatusText.Text = $"Checked {DateTimeOffset.Now:t}" + (reliability.SourceStatus.Concat(dumps.SourceStatus).Concat(updates.SourceStatus).Any(status => status.Contains("unavailable", StringComparison.OrdinalIgnoreCase) || status.Contains("limit reached", StringComparison.OrdinalIgnoreCase)) ? " · Some sources partial" : " · Sources available");
            _healthChecked = DateTimeOffset.Now;
        }
        catch (Exception ex) { HealthStatusText.Text = $"Health summary unavailable: {ex.Message}"; }
        finally { _healthLoading = false; }
    }

    private async Task RefreshAsync()
    {
        if (_loading) return;
        _loading = true;
        RefreshButton.IsEnabled = false;
        InventoryStatus.Text = "Refreshing inventory… Live meters continue updating.";
        try
        {
            _inventory = await SystemInventoryCollector.CollectAsync();
            using var scroll = ScrollPositionKeeper.Capture(this);
            MachineDetails.ItemsSource = _inventory.Machine;
            OsDetails.ItemsSource = _inventory.OperatingSystem;
            var disabled = _inventory.DeviceProblems.Count(device => device.IsDisabled);
            var problems = _inventory.DeviceProblems.Count - disabled;
            DevicesExpander.Header = $"Devices reporting problems · {(_inventory.DevicesAvailable ? $"{CountLabel(problems, "problem")} · {disabled} disabled" : "Unknown / partial")}";
            DeviceDetails.Text = _inventory.DeviceProblems.Count > 0 ? string.Join("\n\n", _inventory.DeviceProblems.Select(device => $"{device.Description}\n{device.DeviceId}")) : _inventory.DevicesAvailable ? "No present devices reported a problem." : "Device status is unavailable.";
            StorageExpander.Header = $"Storage capacity · {(_inventory.StorageAvailable ? $"{_inventory.Volumes.Count} fixed volumes" : "Unknown / partial")}";
            StorageDetails.Text = _inventory.Volumes.Count > 0 ? string.Join("\n\n", _inventory.Volumes.Select(volume => $"{volume.Name} — {Gigabytes(volume.FreeBytes)} free of {Gigabytes(volume.TotalBytes)} ({volume.FreePercent:F1}% free)")) : "Storage capacity is unavailable.";
            AppsExpander.Header = $"Installed apps · {_inventory.Applications.Count}{(_inventory.ApplicationsAvailable ? " registrations" : " found · partial count")}";
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(DevicesExpander, DevicesExpander.Header.ToString());
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(StorageExpander, StorageExpander.Header.ToString());
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(AppsExpander, AppsExpander.Header.ToString());
            RefreshAppList();
            var low = _inventory.Volumes.Count(volume => volume.FreePercent < 10);
            var signals = new List<string>();
            if (!_inventory.DevicesAvailable) signals.Add("Device status unknown");
            else
            {
                if (problems > 0) signals.Add(CountLabel(problems, "device problem"));
                if (disabled > 0) signals.Add($"{disabled} disabled device{(disabled == 1 ? string.Empty : "s")}");
            }
            if (!_inventory.StorageAvailable) signals.Add("Storage status unknown");
            else if (low > 0) signals.Add($"{low} volume{(low == 1 ? string.Empty : "s")} below 10% free");
            if (_inventory.RestartIndicators?.RestartRequested == true) signals.Add(_inventory.RestartIndicators.Status);
            else if (_inventory.RestartIndicators is null) signals.Add("Restart status unknown");
            AttentionHeading.Text = signals.Count == 0 ? "No immediate issues" : "Needs attention";
            AttentionText.Text = signals.Count == 0 ? "Device status, storage capacity, and restart signals look clear in available checks." : string.Join("  ·  ", signals);
            LimitationsText.Text = string.Join("\n", _inventory.Limitations);
            InventoryStatus.Text = $"Inventory checked {_inventory.CollectedAt:HH:mm:ss} · Live meters update every 2 seconds";
            CopyButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            InventoryStatus.Text = _inventory is null ? "Inventory unavailable. Try Refresh." : "Refresh failed. Showing the previous inventory.";
            LimitationsText.Text = $"Inventory error: {ex.Message}";
        }
        finally { _loading = false; RefreshButton.IsEnabled = true; }
    }

    private void AppSearch_TextChanged(object sender, TextChangedEventArgs e) => RefreshAppList();
    private void RefreshAppList()
    {
        if (_inventory is null || AppDetails is null) return;
        var query = AppSearch.Text.Trim();
        var apps = _inventory.Applications.Where(app => app.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) || app.Publisher.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
        AppDetails.Text = apps.Length == 0 ? "No matching applications in the available inventory." : string.Join("\n\n", apps.Select(app => $"{app.Name}  {app.Version}\n{app.Publisher} · {app.Scope}"));
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_inventory is null) return;
        var data = new DataPackage();
        data.SetText($"Skald system summary — {_inventory.CollectedAt:g}\n" + string.Join("\n", _inventory.Machine.Concat(_inventory.OperatingSystem).Select(detail => $"{detail.Label}: {detail.Value}")) + $"\n{LiveContext.Text}\n{AttentionText.Text}\n{ReliabilitySummaryText.Text}\n{LatestProblemText.Text}\n{UpdateSummaryText.Text}\n{HealthStatusText.Text}\n{AppsExpander.Header}\n{StorageDetails.Text}\n{LimitationsText.Text}");
        try
        {
            Clipboard.SetContent(data);
            InventoryStatus.Text = "System summary copied to clipboard.";
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            InventoryStatus.Text = "Clipboard is unavailable. Try copying again.";
        }
    }

    private void Layout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 850;
        for (var index = 0; index < Meters.ColumnDefinitions.Count; index++) Meters.ColumnDefinitions[index].Width = new GridLength(narrow && index >= 2 ? 0 : 1, GridUnitType.Star);
        Grid.SetColumn(DiskTile, narrow ? 0 : 2); Grid.SetRow(DiskTile, narrow ? 1 : 0);
        Grid.SetColumn(NetworkTile, narrow ? 1 : 3); Grid.SetRow(NetworkTile, narrow ? 1 : 0);
        HealthGrid.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 1, GridUnitType.Star);
        Grid.SetColumn(UpdatePanel, narrow ? 0 : 1); Grid.SetRow(UpdatePanel, narrow ? 1 : 0);
        IdentityGrid.ColumnDefinitions[1].Width = new GridLength(narrow ? 0 : 1, GridUnitType.Star);
        Grid.SetColumn(OsPanel, narrow ? 0 : 1); Grid.SetRow(OsPanel, narrow ? 1 : 0);
    }
    private static string CountLabel(int count, string noun) => $"{count} {noun}{(count == 1 ? string.Empty : "s")}";
    private static string Gigabytes(ulong bytes) => $"{bytes / 1073741824d:F1} GB";
    private static string Rate(double bytes) => bytes >= 1048576 ? $"{bytes / 1048576:F1} MB/s" : $"{bytes / 1024:F1} KB/s";
}

