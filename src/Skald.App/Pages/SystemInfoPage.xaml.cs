using Microsoft.UI.Xaml.Controls;
using Skald.Core.Models;

namespace Skald.App.Pages;

public sealed partial class SystemInfoPage : Page, ITelemetryPage
{
    public SystemInfoPage() => InitializeComponent();

    public void Update(SystemMetricsSnapshot snapshot, IReadOnlyList<DiagnosticFinding> findings)
    {
        MachineText.Text = $"Machine: {Environment.MachineName}";
        OsText.Text = $"Operating system: {Environment.OSVersion.VersionString}";
        CpuText.Text = $"Logical processors: {snapshot.Cpu.LogicalProcessorCount}";
        MemoryText.Text = $"Physical memory: {FormatBytes(snapshot.Memory.TotalBytes)}";
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
