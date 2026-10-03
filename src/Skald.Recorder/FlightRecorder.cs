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
    private SessionJournal? _journal;
    // True while Stop is saving: the recording still counts as running (no new Start), but takes no more samples or markers.
    private bool _stopping;

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
            _journal = OpenJournal(_filePath, _metadata, _samples);
        }
    }

    // True while the recording is also being appended to disk as it happens.
    public bool IsJournaling
    {
        get
        {
            lock (_gate)
            {
                return _journal is { Failed: false };
            }
        }
    }

    private static SessionJournal? OpenJournal(string sessionPath, SessionMetadata metadata, List<SystemMetricsSnapshot> preRoll)
    {
        try
        {
            var journal = new SessionJournal(SessionJournal.PathFor(sessionPath), JsonOptions);
            journal.WriteMetadata(metadata);
            foreach (var sample in preRoll) journal.WriteSample(sample);
            return journal;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public void Append(SystemMetricsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        lock (_gate)
        {
            if (_metadata is null || _stopping)
            {
                return;
            }

            if (_samples.Count > 0 && snapshot.Timestamp <= _samples[^1].Timestamp) return;
            _samples.Add(snapshot);
            _journal?.WriteSample(snapshot);
            AddAutomaticEvents(snapshot);
        }
    }

    public void AddMarker(string note = "User reported problem")
    {
        lock (_gate)
        {
            if (_metadata is not null && !_stopping)
            {
                AddEvent(new SessionEvent(DateTimeOffset.UtcNow, SessionEventType.UserMarker, note));
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
                if (_metadata is null || _filePath is null || _stopping) return null;
                // Freeze the recording before the snapshot, so nothing lands in the gap and is lost with the journal. It stays
                // "recording" until the save finishes, so nothing can start a new recording over it.
                _stopping = true;
                filePath = _filePath;
                document = new SessionDocument(_metadata with { EndedAt = DateTimeOffset.UtcNow }, _samples.ToArray(), _events.ToArray());
            }
            try { await SaveAtomicAsync(document, filePath, cancellationToken).ConfigureAwait(false); }
            catch
            {
                // The recording (and its journal) stays live so Stop can be retried.
                lock (_gate) _stopping = false;
                throw;
            }
            SessionJournal? journal;
            lock (_gate)
            {
                journal = _journal;
                _journal = null;
                _metadata = null;
                _filePath = null;
                _stopping = false;
            }
            // The complete file is on disk; the journal has done its job.
            if (journal is not null) await journal.DeleteAsync().ConfigureAwait(false);
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

    // Callers hold _gate.
    private void AddEvent(SessionEvent item)
    {
        _events.Add(item);
        _journal?.WriteEvent(item);
    }

    private void AddAutomaticEvents(SystemMetricsSnapshot snapshot)
    {
        if (snapshot.Cpu.Availability.IsSupported && snapshot.Cpu.UtilizationPercent >= 90)
        {
            AddEvent(new SessionEvent(snapshot.Timestamp, SessionEventType.CpuSaturation, $"CPU {snapshot.Cpu.UtilizationPercent:F1}%"));
        }

        if (snapshot.Memory.Availability.IsSupported && snapshot.Memory.UsedPercent >= 90 && snapshot.Memory.AvailableBytes <= 1024UL * 1024 * 1024)
        {
            AddEvent(new SessionEvent(snapshot.Timestamp, SessionEventType.MemoryPressure, $"Low available memory · {snapshot.Memory.UsedPercent:F1}% occupied; paging pressure not assessed"));
        }

        if (snapshot.Disk.Availability.IsSupported && snapshot.Disk.ActiveTimePercent >= 90)
        {
            AddEvent(new SessionEvent(snapshot.Timestamp, SessionEventType.DiskActivity, $"Disk active {snapshot.Disk.ActiveTimePercent:F1}%"));
        }
    }

    // Journals left behind by a recording that never reached Stop (app crash, freeze, power loss, forced restart).
    public static IReadOnlyList<string> FindInterruptedJournals(string directory)
        => Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*" + SessionJournal.Extension).Where(path => path.EndsWith(".perfsession" + SessionJournal.Extension, StringComparison.OrdinalIgnoreCase)).ToArray() : [];

    // Rebuilds a .perfsession from a journal and removes the journal. Returns null if the journal is empty or still in use.
    // Returns null when nothing new was recovered: the journal is in use, holds nothing usable (it is then removed), or the
    // session file next to it is already at least as complete (a Stop whose journal cleanup failed, or a checkpoint).
    public static async Task<string?> RecoverAsync(string journalPath, CancellationToken cancellationToken = default)
    {
        var (document, inUse) = await SessionJournal.ReadAsync(journalPath, JsonOptions, cancellationToken).ConfigureAwait(false);
        if (inUse) return null;
        var target = journalPath[..^SessionJournal.Extension.Length];
        if (document is not null && File.Exists(target))
        {
            try
            {
                var existing = await LoadAsync(target, cancellationToken).ConfigureAwait(false);
                if (existing.Samples.Count >= document.Samples.Count) document = null;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException) { }
        }
        if (document is not null) await SaveAtomicAsync(document, target, cancellationToken).ConfigureAwait(false);
        await SessionJournal.TryDeleteAsync(journalPath).ConfigureAwait(false);
        return document is null ? null : target;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            // Abandon, do not delete: an unstopped recording is exactly what the journal exists to preserve.
            var journal = _journal;
            _journal = null;
            journal?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        _writeGate.Dispose();
    }
}
