using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Skald.Core.Models;
using Skald.Diagnostics;
using Skald.Triage;

namespace Skald.App.Pages;

public sealed partial class HomePage : Page, ITelemetryPage
{
    private SystemMetricsSnapshot? _latest;
    private IReadOnlyList<DiagnosticFinding> _sustained = [];

    public HomePage()
    {
        InitializeComponent();
        TriageSession.Changed += () => DispatcherQueue.TryEnqueue(RenderHardware);
    }

    public event Action<string>? NavigateRequested;
    public event Action? RunHardwareCheckRequested;
    public event Action? AnalyzeRequested;

    public void Update(SystemMetricsSnapshot snapshot, IReadOnlyList<DiagnosticFinding> findings) => _latest = snapshot;

    // Called every sample with the rolling history. The hand-off uses the same sustained-condition rules as Analyze Performance,
    // so one noisy two-second sample cannot flip the message.
    public void ShowHistory(IReadOnlyList<SystemMetricsSnapshot> history)
    {
        if (history.Count > 0) _latest = history[^1];
        _sustained = WindowDiagnosticAnalyzer.Analyze(history);
        RenderLive();
        RenderHandoff();
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        RenderHardware();
        RenderLive();
        RenderHandoff();
    }

    private void RenderHardware()
    {
        if (HardwareStatus is null) return;
        if (TriageSession.Last is { } last)
        {
            var verdict = last.Report.Verdict;
            HardwareStatus.Text = $"{verdict.Headline} · {last.Report.GeneratedAt.ToLocalTime():g}";
            ViewButton.Visibility = Visibility.Visible;
            RunButton.Content = "Run again";
        }
        else
        {
            HardwareStatus.Text = "Not run yet.";
            ViewButton.Visibility = Visibility.Collapsed;
        }
        RenderHandoff();
    }

    private void RenderLive()
    {
        if (LiveStatus is null) return;
        if (_latest is not { } snapshot) { LiveStatus.Text = "Collecting telemetry…"; return; }
        var cpu = snapshot.Cpu.Availability.IsSupported ? $"CPU {snapshot.Cpu.UtilizationPercent:F0}%" : "CPU unavailable";
        var memory = snapshot.Memory.Availability.IsSupported ? $"Memory {snapshot.Memory.UsedPercent:F0}%" : "Memory unavailable";
        var disk = snapshot.Disk.Availability.IsSupported ? $"Disk {snapshot.Disk.ActiveTimePercent:F0}%" : "Disk unavailable";
        var flagged = _sustained.Count > 0 ? $"\nSustained in the last minute: {string.Join(", ", _sustained.Select(finding => finding.Title))}" : "\nNothing sustained in the last minute.";
        LiveStatus.Text = $"{cpu} · {memory} · {disk}{flagged}";
    }

    private void RenderHandoff()
    {
        if (HandoffHeadline is null) return;
        var statement = Handoff.Compose(TriageSession.Last?.Report, _sustained, TopProcess());
        HandoffHeadline.Text = statement.Headline;
        HandoffDetail.Text = statement.Detail;
    }

    // The process most associated with the top sustained finding; a lead for the technician, not an attribution.
    private string? TopProcess()
    {
        if (_latest is not { } snapshot || _sustained.Count == 0 || snapshot.Processes.Count == 0) return null;
        var title = _sustained[0].Title;
        ProcessMetric? process;
        if (title.Contains("memory", StringComparison.OrdinalIgnoreCase) || title.Contains("commit", StringComparison.OrdinalIgnoreCase))
            process = snapshot.Processes.OrderByDescending(item => item.PrivateMemoryMegabytes).FirstOrDefault();
        else if (title.Contains("disk", StringComparison.OrdinalIgnoreCase) || title.Contains("latency", StringComparison.OrdinalIgnoreCase))
            process = snapshot.Processes.OrderByDescending(item => (item.ReadBytesPerSecond ?? 0) + (item.WriteBytesPerSecond ?? 0)).FirstOrDefault();
        else
            process = snapshot.Processes.Where(item => item.CpuAvailable).OrderByDescending(item => item.CpuPercent).FirstOrDefault();
        return process is null ? null : string.Create(CultureInfo.CurrentCulture, $"{process.Name} (PID {process.ProcessId})");
    }

    private void Layout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var narrow = e.NewSize.Width < 900;
        Grid.SetColumn(SlowCard, narrow ? 0 : 1);
        Grid.SetRow(SlowCard, narrow ? 1 : 0);
        Grid.SetRow(HandoffCard, narrow ? 2 : 1);
        Grid.SetColumnSpan(HandoffCard, narrow ? 1 : 2);
        Cards.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
    }

    private void Run_Click(object sender, RoutedEventArgs e)
    {
        NavigateRequested?.Invoke("triage");
        RunHardwareCheckRequested?.Invoke();
    }

    private void ViewHardware_Click(object sender, RoutedEventArgs e) => NavigateRequested?.Invoke("triage");
    private void Live_Click(object sender, RoutedEventArgs e) => NavigateRequested?.Invoke("performance");
    private void Analyze_Click(object sender, RoutedEventArgs e) => AnalyzeRequested?.Invoke();
}
