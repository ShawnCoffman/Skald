using Skald.Core.Models;
using Skald.Recorder;
using Xunit;

namespace Skald.Core.Tests;

public sealed class FlightRecorderTests
{
    [Fact]
    public async Task WritesAndLoadsPortableSession()
    {
        var path = Path.Combine(Path.GetTempPath(), $"skald-{Guid.NewGuid():N}.perfsession");
        try
        {
            var recorder = new FlightRecorder();
            recorder.Start(path, 2);
            recorder.Append(CreateSnapshot(DateTimeOffset.UtcNow));
            recorder.AddMarker("Test marker");

            var savedPath = await recorder.StopAsync();
            var document = await FlightRecorder.LoadAsync(savedPath!);

            Assert.Equal(path, savedPath);
            Assert.Single(document.Samples);
            Assert.Contains(document.Events, sessionEvent => sessionEvent.Type == SessionEventType.UserMarker);
            Assert.True(document.Metadata.EndedAt >= document.Metadata.StartedAt);
            var replayedProcess = Assert.Single(document.Samples[0].Processes);
            Assert.Equal("Example user", replayedProcess.UserName);
            Assert.Equal(2048, replayedProcess.ReadBytesPerSecond);
            Assert.Equal(12, replayedProcess.ThreadCount);
            Assert.Equal((uint)345, replayedProcess.PageFaultCount);
            Assert.Equal(12.5, replayedProcess.GpuPercent);
            Assert.Equal(12.5, replayedProcess.GpuEngines?["gpu0-3d"]);
            Assert.Equal(256, replayedProcess.GpuDedicatedMegabytes);
            Assert.Equal(64, replayedProcess.GpuSharedMegabytes);
            var replayedGpu = Assert.Single(document.Samples[0].GpuDevices);
            Assert.Equal(42, replayedGpu.TemperatureCelsius);
            Assert.Equal(125.5, replayedGpu.PowerWatts);
            Assert.Equal(1024, Assert.Single(document.Samples[0].GpuAdapters).DedicatedUsedMegabytes);
            Assert.Equal(4200, document.Samples[0].Cpu.ReportedMegahertz);
            Assert.Equal(2300, replayedGpu.GraphicsClockMegahertz);
            Assert.Equal("provider/cpu0/power", Assert.Single(document.Samples[0].Sensors).Id);
            Assert.Equal(45, document.Samples[0].Sensors[0].Value);
            Assert.Equal(18.7, document.Samples[0].Power.BatteryDischargeWatts);
            Assert.Equal(50, document.Samples[0].Power.BatteryFullChargeCapacityWh);
            Assert.Equal(60, document.Samples[0].Power.DisplayBrightnessPercent);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task RecordingIncludesPreRollWithoutDuplicatingLatestSample()
    {
        var path = Path.Combine(Path.GetTempPath(), $"skald-{Guid.NewGuid():N}.perfsession");
        try
        {
            var now = DateTimeOffset.UtcNow;
            var recent = CreateSnapshot(now.AddSeconds(-10));
            var latest = CreateSnapshot(now.AddSeconds(-2));
            var recorder = new FlightRecorder();
            recorder.Start(path, 2, [CreateSnapshot(now.AddMinutes(-6)), recent, latest]);
            recorder.Append(latest);
            recorder.Append(CreateSnapshot(now.AddSeconds(2)));
            recorder.AddMarker();
            var document = await FlightRecorder.LoadAsync((await recorder.StopAsync())!);
            Assert.Equal([recent.Timestamp, latest.Timestamp, now.AddSeconds(2)], document.Samples.Select(sample => sample.Timestamp));
            Assert.Equal(recent.Timestamp, document.Metadata.StartedAt);
            Assert.Contains(document.Events, item => item.Type == SessionEventType.UserMarker);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task MarkerCheckpointCanBeLoadedBeforeRecordingStops()
    {
        var path = Path.Combine(Path.GetTempPath(), $"skald-{Guid.NewGuid():N}.perfsession");
        try
        {
            var recorder = new FlightRecorder();
            var sample = CreateSnapshot(DateTimeOffset.UtcNow.AddSeconds(-2));
            recorder.Start(path, 2, [sample]);
            recorder.AddMarker();
            await recorder.CheckpointAsync();
            var recovered = await FlightRecorder.LoadAsync(path);
            Assert.Single(recovered.Samples);
            Assert.Single(recovered.Events, item => item.Type == SessionEventType.UserMarker);
            Assert.True(recorder.IsRecording);
            await recorder.StopAsync();
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public async Task FailedStopPreservesRecordingForRetry()
    {
        var path = Path.Combine(Path.GetTempPath(), $"skald-{Guid.NewGuid():N}.perfsession");
        Directory.CreateDirectory(path);
        try
        {
            using var recorder = new FlightRecorder();
            recorder.Start(path, 2);
            recorder.Append(CreateSnapshot(DateTimeOffset.UtcNow));

            var error = await Record.ExceptionAsync(() => recorder.StopAsync());
            Assert.True(error is IOException or UnauthorizedAccessException);
            Assert.True(recorder.IsRecording);
            Assert.Equal(1, recorder.SampleCount);

            Directory.Delete(path);
            var savedPath = await recorder.StopAsync();
            Assert.Equal(path, savedPath);
            Assert.Single((await FlightRecorder.LoadAsync(path)).Samples);
        }
        finally
        {
            if (Directory.Exists(path)) Directory.Delete(path);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void RollingBufferKeepsOnlyTheConfiguredWindow()
    {
        var start = DateTimeOffset.UtcNow;
        var buffer = new RollingTelemetryBuffer(TimeSpan.FromSeconds(5));
        buffer.Add(CreateSnapshot(start));
        buffer.Add(CreateSnapshot(start.AddSeconds(3)));
        buffer.Add(CreateSnapshot(start.AddSeconds(7)));

        var samples = buffer.GetSnapshot();

        Assert.Equal(2, samples.Count);
        Assert.Equal(start.AddSeconds(3), samples[0].Timestamp);
    }

    [Fact]
    public async Task ExportsStandaloneHtmlReport()
    {
        var htmlPath = Path.Combine(Path.GetTempPath(), $"skald-{Guid.NewGuid():N}.html");
        try
        {
            var document = new SessionDocument(
                new SessionMetadata(Guid.NewGuid(), "Skald", "0.1", "test-machine", "Windows", DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, 2),
                [CreateSnapshot(DateTimeOffset.UtcNow)],
                [new SessionEvent(DateTimeOffset.UtcNow, SessionEventType.UserMarker, "Test")]);

            await SessionReportWriter.WriteHtmlAsync(document, htmlPath);
            var html = await File.ReadAllTextAsync(htmlPath);

            Assert.Contains("Skald recording report", html);
            Assert.Contains("Test", html);
            // An old recording has no saved Windows reports; the report says so instead of implying nothing happened.
            Assert.Contains("Not saved with this recording", html);
        }
        finally
        {
            if (File.Exists(htmlPath)) File.Delete(htmlPath);
        }
    }

    [Fact]
    public async Task JournalExistsWhileRecordingAndIsRemovedAfterStop()
    {
        var path = Path.Combine(Path.GetTempPath(), $"skald-{Guid.NewGuid():N}.perfsession");
        var journal = path + ".journal";
        try
        {
            var recorder = new FlightRecorder();
            recorder.Start(path, 2);
            Assert.True(recorder.IsJournaling);
            Assert.True(File.Exists(journal));
            recorder.Append(CreateSnapshot(DateTimeOffset.UtcNow));
            Assert.Null(await FlightRecorder.RecoverAsync(journal)); // still open by the live recorder
            Assert.True(File.Exists(journal));
            Assert.Equal(path, await recorder.StopAsync());
            Assert.False(File.Exists(journal));
            Assert.False(recorder.IsJournaling);
            recorder.Dispose();
        }
        finally { File.Delete(path); File.Delete(journal); }
    }

    [Fact]
    public async Task InterruptedRecordingIsRecoveredFromItsJournal()
    {
        var path = Path.Combine(Path.GetTempPath(), $"skald-{Guid.NewGuid():N}.perfsession");
        var journal = path + ".journal";
        try
        {
            var now = DateTimeOffset.UtcNow;
            var recorder = new FlightRecorder();
            recorder.Start(path, 2, [CreateSnapshot(now.AddSeconds(-4))]);
            recorder.Append(CreateSnapshot(now.AddSeconds(-2)));
            recorder.Append(CreateSnapshot(now));
            recorder.AddMarker("froze");
            recorder.Dispose(); // the app dies here: no Stop, no final save
            Assert.False(File.Exists(path));
            Assert.Contains(journal, FlightRecorder.FindInterruptedJournals(Path.GetTempPath()), StringComparer.OrdinalIgnoreCase);

            Assert.Equal(path, await FlightRecorder.RecoverAsync(journal));
            var document = await FlightRecorder.LoadAsync(path);
            Assert.Equal(3, document.Samples.Count);
            Assert.True(document.Metadata.Recovered);
            Assert.Equal(now, document.Metadata.EndedAt);
            Assert.Contains(document.Events, item => item.Type == SessionEventType.UserMarker && item.Note == "froze");
            Assert.False(File.Exists(journal));
        }
        finally { File.Delete(path); File.Delete(journal); }
    }

    [Fact]
    public async Task TruncatedJournalStillRecoversTheSamplesBeforeTheCut()
    {
        var path = Path.Combine(Path.GetTempPath(), $"skald-{Guid.NewGuid():N}.perfsession");
        var journal = path + ".journal";
        try
        {
            var now = DateTimeOffset.UtcNow;
            var recorder = new FlightRecorder();
            recorder.Start(path, 2);
            var lengths = new List<long>();
            for (var i = 0; i < 4; i++)
            {
                recorder.Append(CreateSnapshot(now.AddSeconds(i * 2)));
                lengths.Add(await FlushedLengthAsync(journal, lengths.LastOrDefault()));
            }
            recorder.Dispose();
            // Every entry is flushed as it is written; cut the file just inside the last one, as a power loss mid-write would.
            var bytes = await File.ReadAllBytesAsync(journal);
            await File.WriteAllBytesAsync(journal, bytes[..(int)(lengths[2] + 1)]);

            Assert.Equal(path, await FlightRecorder.RecoverAsync(journal));
            var document = await FlightRecorder.LoadAsync(path);
            Assert.Equal(3, document.Samples.Count);
            Assert.Equal(now, document.Samples[0].Timestamp);
        }
        finally { File.Delete(path); File.Delete(journal); }
    }

    [Fact]
    public async Task CorruptJournalTailStillRecoversTheSamplesBeforeIt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"skald-{Guid.NewGuid():N}.perfsession");
        var journal = path + ".journal";
        try
        {
            var now = DateTimeOffset.UtcNow;
            var recorder = new FlightRecorder();
            recorder.Start(path, 2);
            var lengths = new List<long>();
            for (var i = 0; i < 4; i++)
            {
                recorder.Append(CreateSnapshot(now.AddSeconds(i * 2)));
                lengths.Add(await FlushedLengthAsync(journal, lengths.LastOrDefault()));
            }
            recorder.Dispose();
            // A torn write at power loss can leave stale bytes after the last good entry, not just a short file. Brotli reports
            // that as invalid data; recovery must keep what decoded before it rather than fail (and be retried on every launch).
            var bytes = await File.ReadAllBytesAsync(journal);
            await File.WriteAllBytesAsync(journal, bytes[..(int)lengths[2]].Concat(Enumerable.Repeat((byte)0xFF, 64)).ToArray());

            Assert.Equal(path, await FlightRecorder.RecoverAsync(journal));
            var document = await FlightRecorder.LoadAsync(path);
            // The entry whose compressed bytes share a decoder slice with the corruption may be lost; everything before it is kept.
            Assert.InRange(document.Samples.Count, 2, 3);
            Assert.Equal(now, document.Samples[0].Timestamp);
            Assert.False(File.Exists(journal));
        }
        finally { File.Delete(path); File.Delete(journal); }
    }

    [Fact]
    public async Task FailedStopKeepsTheRecordingLiveForARetry()
    {
        var blocker = Path.Combine(Path.GetTempPath(), $"skald-blocker-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(blocker, "a file where a directory is needed");
        try
        {
            var recorder = new FlightRecorder();
            recorder.Start(Path.Combine(blocker, "session.perfsession"), 2);
            recorder.Append(CreateSnapshot(DateTimeOffset.UtcNow));
            await Assert.ThrowsAnyAsync<IOException>(() => recorder.StopAsync());
            Assert.True(recorder.IsRecording);
            Assert.Equal(1, recorder.SampleCount);
            recorder.Dispose();
        }
        finally { File.Delete(blocker); }
    }

    [Fact]
    public async Task EmptyOrUnrelatedJournalsAreNotRecovered()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"skald-journals-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            var journal = Path.Combine(folder, "empty.perfsession.journal");
            await File.WriteAllBytesAsync(journal, []);
            await File.WriteAllBytesAsync(Path.Combine(folder, "other.journal"), []);
            Assert.Equal([journal], FlightRecorder.FindInterruptedJournals(folder));
            Assert.Null(await FlightRecorder.RecoverAsync(journal));
            Assert.False(File.Exists(journal)); // nothing usable: cleaned up instead of re-read on every launch
            Assert.Empty(FlightRecorder.FindInterruptedJournals(Path.Combine(folder, "does-not-exist")));
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public async Task RecoveryNeverReplacesAMoreCompleteSession()
    {
        var path = Path.Combine(Path.GetTempPath(), $"skald-{Guid.NewGuid():N}.perfsession");
        var journal = path + ".journal";
        try
        {
            var now = DateTimeOffset.UtcNow;
            var complete = new FlightRecorder();
            complete.Start(path, 2);
            for (var i = 0; i < 3; i++) complete.Append(CreateSnapshot(now.AddSeconds(i * 2)));
            await complete.StopAsync();
            complete.Dispose();

            // A leftover journal holding less (for example from a Stop whose journal cleanup failed) must not overwrite the saved file.
            var leftover = new FlightRecorder();
            leftover.Start(path, 2);
            leftover.Append(CreateSnapshot(now));
            leftover.Dispose();

            Assert.Null(await FlightRecorder.RecoverAsync(journal));
            Assert.Equal(3, (await FlightRecorder.LoadAsync(path)).Samples.Count);
            Assert.False(File.Exists(journal));
        }
        finally { File.Delete(path); File.Delete(journal); }
    }

    // The journal is written by a background task; wait until the file grows past the previous entry.
    private static async Task<long> FlushedLengthAsync(string path, long previous)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var length = new FileInfo(path).Length;
            if (length > previous) return length;
            await Task.Delay(10);
        }
        throw new TimeoutException("The journal was not flushed.");
    }

    private static SystemMetricsSnapshot CreateSnapshot(DateTimeOffset timestamp) => new(
        timestamp,
        new CpuMetric(10, 8, MetricAvailability.Supported) { ReportedMegahertz = 4200 },
        new MemoryMetric(16_000, 8_000, 50, MetricAvailability.Supported),
        new DiskMetric(10, 2, MetricAvailability.Supported),
        [new ProcessMetric(42, timestamp, "Example.exe", null, 2, 100, 80)
        {
            UserName = "Example user",
            ReadBytesPerSecond = 2048,
            ThreadCount = 12,
            PageFaultCount = 345,
            GpuEngines = new Dictionary<string, double> { ["gpu0-3d"] = 12.5 },
            GpuDedicatedMegabytes = 256,
            GpuSharedMegabytes = 64
        }])
    {
        Power = new PowerMetric(PowerSource.Battery, 80, null, MetricAvailability.Supported, BatteryDischargeWatts: 18.7)
        { BatteryFullChargeCapacityWh = 50, BatteryDesignCapacityWh = 52, BatteryRemainingCapacityWh = 40, DisplayBrightnessPercent = 60, DisplayActive = true },
        Sensors = [new SensorMetric("provider/cpu0/power", "CPU0", "CPU Package", "Power", "W", 45, "Test provider", "CPU package")],
        GpuDevices = [new GpuDeviceMetric("Test GPU", "NVIDIA NVML", 42, 125.5, 1024, 8192) { GraphicsClockMegahertz = 2300 }],
        GpuAdapters = [new GpuAdapterMemoryMetric("luid_test", 1024, 128)]
    };
}
