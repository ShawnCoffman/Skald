using System.IO.Compression;
using System.Text.Json;
using Skald.Core.Models;
using Skald.Recorder;
using Xunit;

namespace Skald.Core.Tests;

public sealed class RecordingEvidenceTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions LegacyJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Fact]
    public async Task StopSavesTheWindowsReportsMachineAndTracesInTheRecording()
    {
        var path = TempSession();
        try
        {
            var recorder = new FlightRecorder();
            recorder.Start(path, 2);
            recorder.Append(Sample(DateTimeOffset.UtcNow));
            recorder.SetMachine(new SessionMachine(DateTimeOffset.UtcNow, [new InventoryDetail("Make / model", "Contoso · Laptop 7")],
                [new SessionDriver("Display", "Contoso GPU", "Contoso", "31.0.1", null, @"PCI\VEN_1234&DEV_5678\4&1A2B", true)], []));
            recorder.AddTrace(new SessionTrace("x.marker1.etl", DateTimeOffset.UtcNow, "Problem marker 1", 1024));
            SessionMetadata? asked = null;
            await recorder.StopAsync((metadata, _) =>
            {
                asked = metadata;
                return Task.FromResult<SessionWindowsEvidence?>(new(DateTimeOffset.UtcNow, metadata.StartedAt, metadata.EndedAt,
                    [Report(DateTimeOffset.UtcNow, "Applications", "Application Error", "Example.exe")], [], ["test"]));
            });

            var document = await FlightRecorder.LoadAsync(path);
            Assert.NotNull(asked);
            Assert.Equal(document.Metadata.StartedAt, asked!.StartedAt);
            Assert.Equal("Example.exe", Assert.Single(document.WindowsEvidence!.Events).Component);
            Assert.Equal("Contoso · Laptop 7", Assert.Single(document.Machine!.Details).Value);
            Assert.Equal("x.marker1.etl", Assert.Single(document.Traces).FileName);
            Assert.Contains(document.Events, item => item.Type == SessionEventType.DeepTraceSaved);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task FailingEvidenceSourceStillSavesTheRecording()
    {
        var path = TempSession();
        try
        {
            var recorder = new FlightRecorder();
            recorder.Start(path, 2);
            recorder.Append(Sample(DateTimeOffset.UtcNow));
            await recorder.StopAsync((_, _) => throw new TimeoutException("event log is slow"));
            var document = await FlightRecorder.LoadAsync(path);
            Assert.Single(document.Samples);
            Assert.Null(document.WindowsEvidence);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task RecoveredRecordingKeepsMachineAndTracesAndGetsEvidence()
    {
        var path = TempSession();
        try
        {
            var recorder = new FlightRecorder();
            recorder.Start(path, 2);
            recorder.Append(Sample(DateTimeOffset.UtcNow));
            recorder.SetMachine(new SessionMachine(DateTimeOffset.UtcNow, [new InventoryDetail("BIOS version", "Contoso · 1.2.3")], [], []));
            recorder.AddTrace(new SessionTrace("x.marker1.etl", DateTimeOffset.UtcNow, "Problem marker 1", 1));
            recorder.Dispose(); // power loss: no Stop

            Assert.Equal(path, await FlightRecorder.RecoverAsync(path + ".journal", (metadata, _) => Task.FromResult<SessionWindowsEvidence?>(
                new(DateTimeOffset.UtcNow, metadata.StartedAt, metadata.EndedAt.AddHours(1), [Report(metadata.EndedAt.AddMinutes(3), "Crashes & restarts", "Microsoft-Windows-Kernel-Power", "Kernel-Power")], [], []))));
            var document = await FlightRecorder.LoadAsync(path);
            Assert.True(document.Metadata.Recovered);
            Assert.Equal("Contoso · 1.2.3", Assert.Single(document.Machine!.Details).Value);
            Assert.Single(document.Traces);
            Assert.Equal("Crashes & restarts", Assert.Single(document.WindowsEvidence!.Events).Category);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task RecordingsAreBrotliAndOldGzipRecordingsStillLoad()
    {
        var path = TempSession();
        var legacy = TempSession();
        try
        {
            var recorder = new FlightRecorder();
            recorder.Start(path, 2);
            for (var i = 0; i < 30; i++) recorder.Append(Sample(Start.AddSeconds(i * 2)));
            await recorder.StopAsync();
            var bytes = await File.ReadAllBytesAsync(path);
            Assert.False(bytes[0] == 0x1f && bytes[1] == 0x8b);

            var document = await FlightRecorder.LoadAsync(path);
            await using (var file = File.Create(legacy))
            await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
                await JsonSerializer.SerializeAsync(gzip, document, LegacyJson);
            Assert.True(new FileInfo(legacy).Length > bytes.Length);
            Assert.Equal(30, (await FlightRecorder.LoadAsync(legacy)).Samples.Count);
        }
        finally { Cleanup(path); Cleanup(legacy); }
    }

    [Fact]
    public async Task ThresholdConditionsAreLoggedWhenTheyStartAndEndNotEverySample()
    {
        var path = TempSession();
        try
        {
            var recorder = new FlightRecorder();
            recorder.Start(path, 2);
            for (var i = 0; i < 20; i++) recorder.Append(Sample(Start.AddSeconds(i * 2), cpu: i is >= 5 and < 15 ? 97 : 10));
            await recorder.StopAsync();
            var cpu = (await FlightRecorder.LoadAsync(path)).Events.Where(item => item.Type == SessionEventType.CpuSaturation).ToArray();
            Assert.Equal(2, cpu.Length);
            Assert.StartsWith("CPU above 90%", cpu[0].Note);
            Assert.Equal(Start.AddSeconds(10), cpu[0].Timestamp);
            Assert.Contains("ended after 20 s", cpu[1].Note);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task ProcessStartsAndExitsArePairedWithTheirProgram()
    {
        var path = TempSession();
        try
        {
            var editor = new ProcessMetric(200, Start.AddMinutes(-10), "Editor.exe", @"C:\Apps\Editor.exe", 1, 100, 80) { HasVisibleWindow = true };
            var helper = new ProcessMetric(300, Start, "Helper.exe", null, 0, 10, 10);
            var recorder = new FlightRecorder();
            recorder.Start(path, 2);
            recorder.Append(Sample(Start, processes: [Idle, editor]));
            recorder.Append(Sample(Start.AddSeconds(2), processes: [Idle, editor, helper]));
            recorder.Append(Sample(Start.AddSeconds(4), processes: [Idle, helper]));
            // A sample that failed to read processes is not "every process exited".
            recorder.Append(Sample(Start.AddSeconds(6), processes: []));
            await recorder.StopAsync();
            var events = (await FlightRecorder.LoadAsync(path)).Events;
            var launch = Assert.Single(events, item => item.Type == SessionEventType.ProcessLaunch);
            Assert.Equal("Helper.exe", launch.ProcessName);
            var exit = Assert.Single(events, item => item.Type == SessionEventType.ProcessExit);
            Assert.Equal("Editor.exe", exit.ProcessName);
            Assert.Equal(200, exit.ProcessId);
            Assert.True(exit.HadWindow);
            Assert.Equal(Start.AddSeconds(4), exit.Timestamp);
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public async Task PowerSourceAndNetworkChangesAreRecorded()
    {
        var path = TempSession();
        try
        {
            var wifi = new NetworkInterfaceMetric("{wifi}", "Wi-Fi", "Wireless80211", 1_000_000, 0, 0, 0, 0, 0, 0);
            var recorder = new FlightRecorder();
            recorder.Start(path, 2);
            recorder.Append(Sample(Start) with { Power = Power(PowerSource.AC), Network = Network(wifi) });
            recorder.Append(Sample(Start.AddSeconds(2)) with { Power = Power(PowerSource.Battery), Network = Network() });
            await recorder.StopAsync();
            var events = (await FlightRecorder.LoadAsync(path)).Events;
            Assert.Contains(events, item => item.Type == SessionEventType.PowerSourceChanged && item.Note!.Contains("to Battery", StringComparison.Ordinal));
            Assert.Contains(events, item => item.Type == SessionEventType.NetworkChanged && item.Note!.Contains("no longer active: Wi-Fi", StringComparison.Ordinal));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void RedactionRemovesMachineUserAndProfileNamesButKeepsSystemAccounts()
    {
        var document = new SessionDocument(new SessionMetadata(Guid.NewGuid(), "Skald", "1", "PAT-LAPTOP", "Windows", Start, Start.AddMinutes(1), 2),
            [Sample(Start, processes:
            [
                new ProcessMetric(10, Start, "Editor.exe", @"C:\Users\pat.smith\AppData\Local\Editor\Editor.exe", 1, 1, 1) { UserName = @"CORP\pat.smith" },
                new ProcessMetric(4, Start, "svchost.exe", @"C:\Windows\System32\svchost.exe", 1, 1, 1) { UserName = @"NT AUTHORITY\SYSTEM" }
            ])],
            [new SessionEvent(Start, SessionEventType.ProcessExit, @"Editor.exe exited · C:\Users\pat.smith\file.txt on PAT-LAPTOP")])
        {
            Machine = new SessionMachine(Start, [new InventoryDetail("System name", "PAT-LAPTOP"), new InventoryDetail("Make / model", "Contoso · 7")],
                [new SessionDriver("USB", "Contoso Dock", "Contoso", "1.0", null, @"USB\VID_1234&PID_5678\SERIAL0042", true)], []),
            WindowsEvidence = new SessionWindowsEvidence(Start, Start, Start.AddMinutes(1),
                [Report(Start, "Applications", "Application Error", @"C:\Users\pat.smith\AppData\Local\Editor\Editor.exe")],
                [new CrashDumpFile("Current-user app dump", @"C:\Users\pat.smith\AppData\Local\CrashDumps\Editor.exe.10.dmp", Start, 1)], []),
            Traces = [new SessionTrace("x.etl", Start, "End of recording", 1)]
        };

        var redacted = SessionRedactor.Redact(document);
        var json = JsonSerializer.Serialize(redacted);
        Assert.True(redacted.Redacted);
        Assert.DoesNotContain("pat.smith", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PAT-LAPTOP", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SERIAL0042", json, StringComparison.Ordinal);
        Assert.Contains(@"NT AUTHORITY\\SYSTEM", json, StringComparison.Ordinal);
        Assert.Equal("<user>", redacted.Samples[0].Processes[0].UserName);
        Assert.Equal("Contoso · 7", redacted.Machine!.Details[1].Value);
        Assert.Empty(redacted.Traces);
    }

    [Fact]
    public async Task ArchiveCanIncludeTracesOrRedactButNeverRedactsATrace()
    {
        var root = Path.Combine(Path.GetTempPath(), $"skald-archive-{Guid.NewGuid():N}");
        var folder = Path.Combine(root, "Skald Sessions");
        Directory.CreateDirectory(folder);
        try
        {
            var path = Path.Combine(folder, "run.perfsession");
            var recorder = new FlightRecorder();
            recorder.Start(path, 2);
            recorder.Append(Sample(Start, processes: [new ProcessMetric(10, Start, "Editor.exe", @"C:\Users\pat.smith\Editor.exe", 1, 1, 1) { UserName = @"CORP\pat.smith" }]));
            await File.WriteAllTextAsync(Path.Combine(folder, "run.marker1.etl"), "etl");
            recorder.AddTrace(new SessionTrace("run.marker1.etl", Start, "Problem marker 1", 3));
            await recorder.StopAsync();

            var withTraces = Path.Combine(root, "with.zip");
            await SessionArchive.CreateAsync([path], withTraces, new SessionArchiveOptions { IncludeTraces = true });
            using (var zip = ZipFile.OpenRead(withTraces))
                Assert.Equal(["Skald Sessions/run.marker1.etl", "Skald Sessions/run.perfsession"], zip.Entries.Select(entry => entry.FullName).Order());

            var redacted = Path.Combine(root, "redacted.zip");
            await SessionArchive.CreateAsync([path], redacted, new SessionArchiveOptions { IncludeTraces = true, Redact = true });
            using (var zip = ZipFile.OpenRead(redacted))
            {
                var entry = Assert.Single(zip.Entries);
                var extracted = Path.Combine(root, "extracted.perfsession");
                entry.ExtractToFile(extracted);
                var document = await FlightRecorder.LoadAsync(extracted);
                Assert.True(document.Redacted);
                Assert.Equal("<user>", document.Samples[0].Processes[0].UserName);
                Assert.DoesNotContain("pat.smith", document.Samples[0].Processes[0].ExecutablePath, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(@"C:\Users\pat\OneDrive - Contoso\Documents", true)]
    [InlineData(@"C:\Users\pat\OneDrive - Contoso", true)]
    [InlineData(@"C:\Users\pat\Documents", false)]
    [InlineData(@"C:\Users\pat\OneDrive - Contoso Archive\Documents", false)]
    public void DocumentsInsideAOneDriveRootCountAsSynced(string documents, bool synced)
        => Assert.Equal(synced, SessionLocations.IsCloudSynced(documents, [null, @"C:\Users\pat\OneDrive - Contoso", ""]));

    [Fact]
    public void RunPowerReportsCpuPackageAndGpuBoardSeparately()
    {
        var samples = Enumerable.Range(0, 31).Select(index => Sample(Start.AddSeconds(index * 2)) with
        {
            Sensors = [new SensorMetric("windows/energy-meter/RAPL_Package0_PKG", "CPU 0", "CPU package power", "Power", "W", 60, "Windows Energy Meter (RAPL)", "CPU package")],
            GpuDevices = [new GpuDeviceMetric("GPU", "NVIDIA NVML", 50, index < 15 ? 100 : 300, null, null) { DeviceId = "gpu-1" }]
        }).ToArray();
        var summary = PowerSessionSummary.From(new SessionDocument(new SessionMetadata(Guid.NewGuid(), "Skald", "1", "pc", "Windows", Start, Start.AddMinutes(1), 2), samples, []));
        var cpu = Assert.Single(summary.Domains, item => item.Scope == "CPU package");
        Assert.Equal(60, cpu.AverageWatts, 3);
        Assert.Equal(1.0, cpu.EnergyWh, 3); // 60 W for one minute
        Assert.Equal(100, cpu.CoveragePercent, 3);
        var gpu = Assert.Single(summary.Domains, item => item.Scope == "GPU board");
        Assert.Equal(300, gpu.PeakWatts);
        Assert.InRange(gpu.AverageWatts, 190, 210);
        Assert.Null(summary.AverageDischargeWatts);
    }

    [Fact]
    public void ReportShowsSavedWindowsReportsAndProgramExitsAroundTheMarker()
    {
        var exit = new SessionEvent(Start.AddSeconds(30), SessionEventType.ProcessExit, "Editor.exe (PID 200) exited · had a window") { ProcessName = "Editor.exe", ProcessId = 200, HadWindow = true };
        var document = new SessionDocument(new SessionMetadata(Guid.NewGuid(), "Skald", "1", "pc", "Windows", Start, Start.AddMinutes(1), 2),
            Enumerable.Range(0, 31).Select(index => Sample(Start.AddSeconds(index * 2))).ToArray(),
            [new SessionEvent(Start.AddSeconds(40), SessionEventType.UserMarker, "User reported problem"), exit])
        {
            WindowsEvidence = new SessionWindowsEvidence(Start, Start, Start.AddMinutes(1), [Report(Start.AddSeconds(31), "Applications", "Application Error", "Editor.exe")], [], [])
        };
        var html = SessionReportWriter.ToHtml(document);
        Assert.Contains("Windows reports during the recording", html);
        Assert.Contains("Application Error", html);
        Assert.Contains("Programs that exited", html);
        Assert.Contains("Editor.exe (PID 200) exited", html);
        Assert.Contains("badge found", html);
    }

    private static readonly ProcessMetric Idle = new(0, null, "Idle", null, 0, 0, 0);

    private static PowerMetric Power(PowerSource source) => new(source, 80, null, MetricAvailability.Supported);

    private static NetworkMetric Network(params NetworkInterfaceMetric[] interfaces)
        => new(0, 0, interfaces.Length, MetricAvailability.Supported) { Interfaces = interfaces };

    private static ReliabilityEvent Report(DateTimeOffset time, string category, string provider, string component)
        => new(time, "Application", 1, provider, 1000, category, "Error", component, $"{provider} for {component}", "details", "<Event/>");

    private static SystemMetricsSnapshot Sample(DateTimeOffset time, double cpu = 10, IReadOnlyList<ProcessMetric>? processes = null) => new(time,
        new CpuMetric(cpu, 8, MetricAvailability.Supported), new MemoryMetric(16_000_000_000, 8_000_000_000, 50, MetricAvailability.Supported),
        new DiskMetric(0, 0, MetricAvailability.Supported), processes ?? [Idle]);

    private static string TempSession() => Path.Combine(Path.GetTempPath(), $"skald-{Guid.NewGuid():N}.perfsession");

    private static void Cleanup(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        if (File.Exists(path + ".journal")) File.Delete(path + ".journal");
    }
}
