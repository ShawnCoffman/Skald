using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Skald.Core.Models;
using Windows.UI;

namespace Skald.App.Pages;

public sealed partial class ProcessesPage : Page, ITelemetryPage
{
    private readonly Dictionary<string, bool> _expandedGroups = new(StringComparer.OrdinalIgnoreCase);
    private SystemMetricsSnapshot? _snapshot;
    private DateTimeOffset? _lastRowsRenderedAt;
    private ProcessSelection? _selectedProcess;
    private string _sortColumn = "Memory";
    private bool _sortDescending = true;
    private bool _refreshingRows;
    private string[] _columns = ["CPU", "Memory", "GPU", "Read", "Write"];
    private bool _syncingScroll;
    private double _metricOffset;

    public ProcessesPage()
    {
        InitializeComponent();
        SortDescriptionText.Text = "Memory ↓";
        BuildHeaders();
    }

    public void Update(SystemMetricsSnapshot snapshot, IReadOnlyList<DiagnosticFinding> findings)
    {
        _snapshot = snapshot;
        if (_lastRowsRenderedAt is not { } previous || snapshot.Timestamp - previous >= TimeSpan.FromSeconds(5)
            || snapshot.Timestamp < previous)
        {
            RefreshRows();
            _lastRowsRenderedAt = snapshot.Timestamp;
        }
    }

    private void Page_Loaded(object sender, RoutedEventArgs e)
    {
        RefreshRows();
        _lastRowsRenderedAt = _snapshot?.Timestamp;
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshRows();

    private void SortHeader_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string column }) return;
        if (_sortColumn == column) _sortDescending = !_sortDescending;
        else
        {
            _sortColumn = column;
            _sortDescending = column is not ("Name" or "User" or "PID");
        }

        SortDescriptionText.Text = $"{column} {(_sortDescending ? "↓" : "↑")}";
        RefreshRows();
    }

    private void GroupToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;
        var defaultExpanded = FilterBox.Text.Trim().Length > 0;
        var expanded = _expandedGroups.TryGetValue(key, out var stored) ? stored : defaultExpanded;
        _expandedGroups[key] = !expanded;
        RefreshRows();
    }

    private void RefreshRows()
    {
        if (_snapshot is null || ProcessList is null || FilterBox is null) return;

        var query = FilterBox.Text.Trim();
        var groups = ProcessGrouping.Create(_snapshot.Processes)
            .Select(group => FilterGroup(group, query))
            .OfType<ProcessGroup>()
            .ToArray();

        var rows = new List<ProcessTableRow>();
        AppendSection(rows, "APPS", groups.Where(group => group.IsApplication), query);
        AppendSection(rows, "BACKGROUND PROCESSES", groups.Where(group => !group.IsApplication), query);
        if (rows.Count == 0) rows.Add(ProcessTableRow.ForSection("No matching processes"));
        foreach (var row in rows) row.Cells = _columns.Select(column => new CellValue(row.Cell(column))).ToArray();

        var oldScrollViewer = FindScrollViewer(ProcessList);
        var horizontalOffset = oldScrollViewer?.HorizontalOffset ?? 0;
        var verticalOffset = oldScrollViewer?.VerticalOffset ?? 0;
        _refreshingRows = true;
        try
        {
            ProcessList.ItemsSource = rows;
            if (_selectedProcess is { } selected)
            {
                ProcessList.SelectedItem = rows.FirstOrDefault(row =>
                    row.Process is { } process &&
                    process.ProcessId == selected.ProcessId &&
                    process.StartTime == selected.StartTime);
                if (ProcessList.SelectedItem is null) _selectedProcess = null;
            }
            ProcessList.UpdateLayout();
            if (FindScrollViewer(ProcessList) is { } scrollViewer)
                _ = scrollViewer.ChangeView(horizontalOffset, verticalOffset, null, true);
        }
        finally
        {
            _refreshingRows = false;
        }

        if (_selectedProcess is null) SelectedDetailsText.Text = "Select a process to see its path and start time.";
        CountText.Text = $"{groups.Sum(group => group.Processes.Count)} of {_snapshot.Processes.Count} processes · {groups.Length} groups · table every 5 s (telemetry every 2 s)";
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        if (parent is ScrollViewer scrollViewer) return scrollViewer;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(parent, index)) is { } child)
                return child;
        }
        return null;
    }

    private static ProcessGroup? FilterGroup(ProcessGroup group, string query)
    {
        if (query.Length == 0 || group.Name.Contains(query, StringComparison.OrdinalIgnoreCase)) return group;
        var matches = group.Processes.Where(process =>
            process.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            (process.UserName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
            process.ProcessId.ToString(CultureInfo.InvariantCulture).Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return matches.Length == 0 ? null : group with { Processes = matches };
    }

    private void AppendSection(List<ProcessTableRow> rows, string title, IEnumerable<ProcessGroup> sectionGroups, string query)
    {
        var groups = SortGroups(sectionGroups).ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        if (groups.Length == 0) return;
        rows.Add(ProcessTableRow.ForSection($"{title} ({groups.Length})"));

        foreach (var group in groups)
        {
            var members = SortProcesses(group.Processes).ThenBy(process => process.Name, StringComparer.OrdinalIgnoreCase).ToArray();
            if (members.Length == 1)
            {
                rows.Add(ProcessTableRow.ForProcess(members[0], false));
                continue;
            }

            var expanded = _expandedGroups.TryGetValue(group.Key, out var stored) ? stored : query.Length > 0;
            rows.Add(ProcessTableRow.ForGroup(group, expanded));
            if (!expanded) continue;
            foreach (var process in members) rows.Add(ProcessTableRow.ForProcess(process, true));
        }
    }

    private IOrderedEnumerable<ProcessGroup> SortGroups(IEnumerable<ProcessGroup> groups)
        => _sortColumn switch
        {
            "Name" => SortBy(groups, group => group.Name.ToUpperInvariant()),
            "PID" => SortBy(groups, group => group.PrimaryProcessId),
            "User" => SortBy(groups, group => group.UserName?.ToUpperInvariant()),
            "CPU" => SortBy(groups, group => group.CpuPercent),
            "GPU" => SortBy(groups, group => group.GpuPercent),
            "GpuDedicated" => SortBy(groups, group => group.GpuDedicatedMegabytes),
            "GpuShared" => SortBy(groups, group => group.GpuSharedMegabytes),
            "Private" => SortBy(groups, group => group.PrivateMemoryMegabytes),
            "Read" => SortBy(groups, group => group.ReadBytesPerSecond),
            "Write" => SortBy(groups, group => group.WriteBytesPerSecond),
            "Threads" => SortBy(groups, group => group.ThreadCount),
            "Handles" => SortBy(groups, group => group.HandleCount),
            "Peak" => SortBy(groups, group => group.PeakWorkingSetMegabytes),
            "Time" => SortBy(groups, group => group.TotalCpuTime),
            "Faults" => SortBy(groups, group => group.PageFaultCount),
            _ => SortBy(groups, group => group.WorkingSetMegabytes)
        };

    private IOrderedEnumerable<ProcessMetric> SortProcesses(IEnumerable<ProcessMetric> processes)
        => _sortColumn switch
        {
            "Name" => SortBy(processes, process => process.Name.ToUpperInvariant()),
            "PID" => SortBy(processes, process => process.ProcessId),
            "User" => SortBy(processes, process => process.UserName?.ToUpperInvariant()),
            "CPU" => SortBy(processes, process => process.CpuAvailable ? process.CpuPercent : (double?)null),
            "GPU" => SortBy(processes, process => process.GpuPercent),
            "GpuDedicated" => SortBy(processes, process => process.GpuDedicatedMegabytes),
            "GpuShared" => SortBy(processes, process => process.GpuSharedMegabytes),
            "Private" => SortBy(processes, process => process.PrivateMemoryAvailable ? process.PrivateMemoryMegabytes : (double?)null),
            "Read" => SortBy(processes, process => process.ReadBytesPerSecond),
            "Write" => SortBy(processes, process => process.WriteBytesPerSecond),
            "Threads" => SortBy(processes, process => process.ThreadCount),
            "Handles" => SortBy(processes, process => process.HandleCount),
            "Peak" => SortBy(processes, process => process.PeakWorkingSetMegabytes),
            "Time" => SortBy(processes, process => process.TotalCpuTime),
            "Faults" => SortBy(processes, process => process.PageFaultCount),
            _ => SortBy(processes, process => process.WorkingSetAvailable ? process.WorkingSetMegabytes : (double?)null)
        };

    private IOrderedEnumerable<T> SortBy<T, TKey>(IEnumerable<T> source, Func<T, TKey> key)
        => _sortDescending ? source.OrderByDescending(key) : source.OrderBy(key);

    private void ProcessList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingRows) return;
        if (ProcessList.SelectedItem is not ProcessTableRow row)
        {
            _selectedProcess = null;
            SelectedDetailsText.Text = "Select a process to see its path and start time.";
            return;
        }

        if (row.Process is { } process)
        {
            _selectedProcess = new ProcessSelection(process.ProcessId, process.StartTime);
            var started = process.StartTime?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.CurrentCulture) ?? "unavailable";
            SelectedDetailsText.Text = $"{process.Name} · PID {process.ProcessId} · Started {started} · {process.ExecutablePath ?? "Path unavailable"}";
        }
        else if (row.Group is { } group)
        {
            _selectedProcess = null;
            SelectedDetailsText.Text = $"{group.Name} · {group.Processes.Count} related processes · {group.Processes[0].ExecutablePath ?? "Path unavailable"}";
        }
    }

    private readonly record struct ProcessSelection(int ProcessId, DateTimeOffset? StartTime);

    private void PresetPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MetricHeaders is null) return;
        _columns = PresetPicker.SelectedIndex switch
        {
            1 => ["CPU", "Threads", "Handles", "Time", "User"],
            2 => ["Memory", "Private", "Peak", "Faults"],
            3 => ["GPU", "GpuDedicated", "GpuShared", "Memory"],
            4 => ["Read", "Write", "CPU", "User"],
            _ => ["CPU", "Memory", "GPU", "Read", "Write"]
        };
        _metricOffset = 0;
        _sortColumn = _columns[0];
        _sortDescending = true;
        SortDescriptionText.Text = $"{_sortColumn} ↓";
        BuildHeaders();
        RefreshRows();
    }

    private void BuildHeaders()
    {
        MetricHeaders.Children.Clear();
        foreach (var column in _columns)
        {
            var label = column switch { "GpuDedicated" => "Dedicated GPU", "GpuShared" => "Shared GPU", "Read" => "I/O read", "Write" => "I/O write", "Time" => "CPU time", "Peak" => "Peak RAM", _ => column };
            var button = new Button { Content = label, Tag = column, Width = 120, HorizontalContentAlignment = HorizontalAlignment.Left, Padding = new Thickness(0), BorderThickness = new Thickness(0), Background = new SolidColorBrush(Color.FromArgb(0,0,0,0)) };
            button.Click += SortHeader_Click;
            MetricHeaders.Children.Add(button);
        }
    }

    private void Metrics_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is ScrollViewer viewer) { _syncingScroll = true; viewer.ChangeView(_metricOffset, null, null, true); _syncingScroll = false; }
    }

    private void Metrics_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_syncingScroll || sender is not ScrollViewer source || ProcessList is null) return;
        // Only the header controls horizontal position; row virtualization cannot reset the global offset.
        if (!ReferenceEquals(source, HeaderScroll)) return;
        _metricOffset = source.HorizontalOffset;
        _syncingScroll = true;
        foreach (var viewer in MetricScrollers(ProcessList)) viewer.ChangeView(_metricOffset, null, null, true);
        _syncingScroll = false;
    }

    private static IEnumerable<ScrollViewer> MetricScrollers(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer { Tag: "metrics" } viewer) yield return viewer;
            foreach (var nested in MetricScrollers(child)) yield return nested;
        }
    }

    private sealed class ProcessTableRow
    {
        public CellValue[] Cells { get; set; } = [];
        public string Cell(string column) => column switch
        {
            "CPU" => CpuTableDisplay, "Memory" => WorkingSetDisplay, "GPU" => GpuDisplay,
            "GpuDedicated" => GpuDedicatedDisplay, "GpuShared" => GpuSharedDisplay,
            "Private" => PrivateMemoryDisplay, "Read" => ReadDisplay, "Write" => WriteDisplay,
            "Threads" => ThreadDisplay, "Handles" => HandleDisplay, "Peak" => PeakMemoryDisplay,
            "Time" => CpuTimeDisplay, "Faults" => PageFaultDisplay, "User" => UserDisplay, _ => ""
        };
        private static readonly SolidColorBrush SectionBackground = new(Color.FromArgb(255, 26, 48, 58));
        private static readonly SolidColorBrush GroupBackground = new(Color.FromArgb(255, 24, 45, 54));
        private static readonly SolidColorBrush ProcessBackground = new(Color.FromArgb(255, 19, 36, 46));

        public ProcessMetric? Process { get; private init; }
        public ProcessGroup? Group { get; private init; }
        public string? GroupKey => Group?.Key;
        public Visibility GroupVisibility => Group is null ? Visibility.Collapsed : Visibility.Visible;
        public Visibility TextVisibility => Group is null ? Visibility.Visible : Visibility.Collapsed;
        public Brush RowBackground { get; private init; } = ProcessBackground;
        public Thickness NameMargin { get; private init; } = new(0);
        public string GroupLabel { get; private init; } = string.Empty;
        public string Name { get; private init; } = string.Empty;
        public string ProcessIdText { get; private init; } = string.Empty;
        public string UserDisplay { get; private init; } = string.Empty;
        public string CpuTableDisplay { get; private init; } = string.Empty;
        public string GpuDisplay { get; private init; } = string.Empty;
        public string GpuDedicatedDisplay { get; private init; } = string.Empty;
        public string GpuSharedDisplay { get; private init; } = string.Empty;
        public string WorkingSetDisplay { get; private init; } = string.Empty;
        public string PrivateMemoryDisplay { get; private init; } = string.Empty;
        public string ReadDisplay { get; private init; } = string.Empty;
        public string WriteDisplay { get; private init; } = string.Empty;
        public string ThreadDisplay { get; private init; } = string.Empty;
        public string HandleDisplay { get; private init; } = string.Empty;
        public string PeakMemoryDisplay { get; private init; } = string.Empty;
        public string CpuTimeDisplay { get; private init; } = string.Empty;
        public string PageFaultDisplay { get; private init; } = string.Empty;

        public static ProcessTableRow ForSection(string title) => new()
        {
            Name = title,
            RowBackground = SectionBackground
        };

        public static ProcessTableRow ForGroup(ProcessGroup group, bool expanded) => new()
        {
            Group = group,
            GroupLabel = $"{(expanded ? "▾" : "▸")}  {group.Name} ({group.Processes.Count})",
            RowBackground = GroupBackground,
            ProcessIdText = group.PrimaryProcessId.ToString(CultureInfo.InvariantCulture),
            UserDisplay = group.UserName ?? "—",
            CpuTableDisplay = FormatPercent(group.CpuPercent),
            GpuDisplay = FormatPercent(group.GpuPercent),
            GpuDedicatedDisplay = FormatMemory(group.GpuDedicatedMegabytes),
            GpuSharedDisplay = FormatMemory(group.GpuSharedMegabytes),
            WorkingSetDisplay = FormatMemory(group.WorkingSetMegabytes),
            PrivateMemoryDisplay = FormatMemory(group.PrivateMemoryMegabytes),
            ReadDisplay = FormatRate(group.ReadBytesPerSecond),
            WriteDisplay = FormatRate(group.WriteBytesPerSecond),
            ThreadDisplay = FormatCount(group.ThreadCount),
            HandleDisplay = FormatCount(group.HandleCount),
            PeakMemoryDisplay = FormatMemory(group.PeakWorkingSetMegabytes),
            CpuTimeDisplay = FormatTime(group.TotalCpuTime),
            PageFaultDisplay = FormatCount(group.PageFaultCount)
        };

        public static ProcessTableRow ForProcess(ProcessMetric process, bool child) => new()
        {
            Process = process,
            Name = process.Name,
            NameMargin = child ? new Thickness(28, 0, 0, 0) : new Thickness(0),
            ProcessIdText = process.ProcessId.ToString(CultureInfo.InvariantCulture),
            UserDisplay = process.UserDisplay,
            CpuTableDisplay = process.CpuTableDisplay,
            GpuDisplay = process.GpuDisplay,
            GpuDedicatedDisplay = process.GpuDedicatedDisplay,
            GpuSharedDisplay = process.GpuSharedDisplay,
            WorkingSetDisplay = process.WorkingSetDisplay,
            PrivateMemoryDisplay = process.PrivateMemoryDisplay,
            ReadDisplay = process.ReadDisplay,
            WriteDisplay = process.WriteDisplay,
            ThreadDisplay = process.ThreadDisplay,
            HandleDisplay = process.HandleDisplay,
            PeakMemoryDisplay = process.PeakMemoryDisplay,
            CpuTimeDisplay = process.CpuTimeDisplay,
            PageFaultDisplay = process.PageFaultDisplay
        };

        private static string FormatPercent(double? value) => value is { } number ? $"{number:F1}%" : "—";
        private static string FormatMemory(double? value) => value is { } number ? $"{number:F0} MB" : "—";
        private static string FormatCount(long? value) => value?.ToString("N0", CultureInfo.CurrentCulture) ?? "—";
        private static string FormatTime(TimeSpan? value)
            => value is { } time ? $"{(int)time.TotalHours:00}:{time.Minutes:00}:{time.Seconds:00}" : "—";

        private static string FormatRate(double? bytesPerSecond)
        {
            if (bytesPerSecond is null) return "—";
            var value = bytesPerSecond.Value;
            var suffix = "B/s";
            if (value >= 1024) { value /= 1024; suffix = "KB/s"; }
            if (value >= 1024) { value /= 1024; suffix = "MB/s"; }
            return $"{value:F1} {suffix}";
        }
    }

    private sealed record CellValue(string Value);
}
