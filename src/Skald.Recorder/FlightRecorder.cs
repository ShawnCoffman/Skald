using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Skald.Core.Models;

namespace Skald.Recorder;

public sealed class FlightRecorder : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General)
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly object _gate = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly List<SystemMetricsSnapshot> _samples = [];
    private readonly List<SessionEvent> _events = [];
    private string? _filePath;
    private SessionMetadata? _metadata;

    public bool IsRecording
    {
        get
        {
            lock (_gate)
            {
                return _metadata is not null;
            }
        }
    }

    public DateTimeOffset? StartedAt
    {
        get
        {
            lock (_gate)
            {
                return _metadata?.StartedAt;
            }
        }
    }

    public int SampleCount
    {
        get
        {
            lock (_gate)
            {
                return _samples.Count;
            }
        }
    }

    public void Start(string filePath, int sampleIntervalSeconds, IReadOnlyList<SystemMetricsSnapshot>? preRoll = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        lock (_gate)
        {
            if (_metadata is not null)
            {
                throw new InvalidOperationException("A recording is already in progress.");
            }

            var startedAt = DateTimeOffset.UtcNow;
            _filePath = Path.GetFullPath(filePath);
            _samples.Clear();
            _events.Clear();
            if (preRoll is not null)
            {
                foreach (var sample in preRoll.Where(sample => sample.Timestamp <= startedAt && sample.Timestamp >= startedAt.AddMinutes(-5))
                             .OrderBy(sample => sample.Timestamp).DistinctBy(sample => sample.Timestamp))
                    _samples.Add(sample);
            }
            _metadata = new SessionMetadata(
                Guid.NewGuid(),
                "Skald",
                typeof(FlightRecorder).Assembly.GetName().Version?.ToString() ?? "0.1.0",
                Environment.MachineName,
                Environment.OSVersion.VersionString,
                _samples.Count > 0 ? _samples[0].Timestamp : startedAt,
                startedAt,
                sampleIntervalSeconds);
        }
    }

    public void Append(SystemMetricsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_gate)
        {
            if (_metadata is null)
            {
                return;
            }

            if (_samples.Count > 0 && snapshot.Timestamp <= _samples[^1].Timestamp) return;
            _samples.Add(snapshot);
            AddAutomaticEvents(snapshot);
        }
    }

    public void AddMarker(string note = "User reported problem")
    {
        lock (_gate)
        {
            if (_metadata is not null)
            {
                _events.Add(new SessionEvent(DateTimeOffset.UtcNow, SessionEventType.UserMarker, note));
            }
        }
    }

    public async Task<string?> StopAsync(CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SessionDocument document;
            string filePath;
            lock (_gate)
            {
                if (_metadata is null || _filePath is null) return null;
                var endedAt = DateTimeOffset.UtcNow;
                document = new SessionDocument(_metadata with { EndedAt = endedAt }, _samples.ToArray(), _events.ToArray());
                filePath = _filePath;
            }
            await SaveAtomicAsync(document, filePath, cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                _metadata = null;
                _filePath = null;
            }
            return filePath;
        }
        finally { _writeGate.Release(); }
    }

    public async Task<string?> CheckpointAsync(CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SessionDocument document;
            string filePath;
            lock (_gate)
            {
                if (_metadata is null || _filePath is null) return null;
                document = new SessionDocument(_metadata with { EndedAt = DateTimeOffset.UtcNow }, _samples.ToArray(), _events.ToArray());
                filePath = _filePath;
            }
            await SaveAtomicAsync(document, filePath, cancellationToken).ConfigureAwait(false);
            return filePath;
        }
        finally { _writeGate.Release(); }
    }

    private static async Task SaveAtomicAsync(SessionDocument document, string filePath, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = filePath + ".writing";
        try
        {
            await using (var file = File.Create(temporary))
            await using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
                await JsonSerializer.SerializeAsync(gzip, document, JsonOptions, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, filePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static async Task<SessionDocument> LoadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        await using var file = File.OpenRead(filePath);
        await using var gzip = new GZipStream(file, CompressionMode.Decompress);
        var document = await JsonSerializer.DeserializeAsync<SessionDocument>(gzip, JsonOptions, cancellationToken);
        return document ?? throw new InvalidDataException("The session file did not contain a session document.");
    }

    private void AddAutomaticEvents(SystemMetricsSnapshot snapshot)
    {
        if (snapshot.Cpu.Availability.IsSupported && snapshot.Cpu.UtilizationPercent >= 90)
        {
            _events.Add(new SessionEvent(snapshot.Timestamp, SessionEventType.CpuSaturation, $"CPU {snapshot.Cpu.UtilizationPercent:F1}%"));
        }

        if (snapshot.Memory.Availability.IsSupported && snapshot.Memory.UsedPercent >= 90 && snapshot.Memory.AvailableBytes <= 1024UL * 1024 * 1024)
        {
            _events.Add(new SessionEvent(snapshot.Timestamp, SessionEventType.MemoryPressure, $"Low available memory · {snapshot.Memory.UsedPercent:F1}% occupied; paging pressure not assessed"));
        }

        if (snapshot.Disk.Availability.IsSupported && snapshot.Disk.ActiveTimePercent >= 90)
        {
            _events.Add(new SessionEvent(snapshot.Timestamp, SessionEventType.DiskActivity, $"Disk active {snapshot.Disk.ActiveTimePercent:F1}%"));
        }
    }

    public void Dispose() => _writeGate.Dispose();
}
