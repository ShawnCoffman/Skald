using System.Text.Json;
using System.Text.Json.Serialization;
using Skald.Core.Models;

namespace Skald.Recorder;

// Collects the Windows evidence for a recording's time window; called while Stop or recovery saves the session. Returning null
// (or throwing) saves the session without it.
public delegate Task<SessionWindowsEvidence?> SessionEvidenceSource(SessionMetadata metadata, CancellationToken cancellationToken);

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
    private readonly List<SessionTrace> _traces = [];
    private readonly Dictionary<SessionEventType, DateTimeOffset> _activeConditions = [];
    // Process names, paths and user names repeat in every sample; one shared copy keeps a long recording's memory down.
    private readonly Dictionary<string, string> _strings = new(StringComparer.Ordinal);
    private SystemMetricsSnapshot? _previous;
    private SessionMachine? _machine;
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

    // Time of the first saved sample, including pre-roll.
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

    public string? FilePath
    {
        get
        {
            lock (_gate)
            {
                return _filePath;
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

    public int MarkerCount
    {
        get
        {
            lock (_gate)
            {
                return _events.Count(item => item.Type == SessionEventType.UserMarker);
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
            _traces.Clear();
            _activeConditions.Clear();
            _strings.Clear();
            _previous = null;
            _machine = null;
            if (preRoll is not null)
            {
                foreach (var sample in preRoll.Where(sample => sample.Timestamp <= startedAt && sample.Timestamp >= startedAt.AddMinutes(-5))
                             .OrderBy(sample => sample.Timestamp).DistinctBy(sample => sample.Timestamp))
                    _samples.Add(Compact(sample));
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
            // Pre-roll gets the same automatic events as live samples, so the minutes before Record are just as readable.
            foreach (var sample in _samples) AddAutomaticEvents(sample);
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
            var sample = Compact(snapshot);
            _samples.Add(sample);
            _journal?.WriteSample(sample);
            AddAutomaticEvents(sample);
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

    // Machine identity can take several seconds to read; it is attached whenever it arrives.
    public void SetMachine(SessionMachine machine)
    {
        ArgumentNullException.ThrowIfNull(machine);
        lock (_gate)
        {
            if (_metadata is null) return;
            _machine = machine;
            _journal?.WriteMachine(machine);
        }
    }

    // Records a deep trace saved beside the session. Accepted while Stop is saving, so the final trace is part of the session.
    public void AddTrace(SessionTrace trace)
    {
        ArgumentNullException.ThrowIfNull(trace);
        lock (_gate)
        {
            if (_metadata is null) return;
            _traces.Add(trace);
            _journal?.WriteTrace(trace);
            AddEvent(new SessionEvent(trace.SavedAt, SessionEventType.DeepTraceSaved, $"{trace.Reason} · {trace.FileName}"));
        }
    }

    public async Task<string?> StopAsync(SessionEvidenceSource? evidence = null, CancellationToken cancellationToken = default)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SessionMetadata metadata;
            string filePath;
            lock (_gate)
            {
                if (_metadata is null || _filePath is null || _stopping) return null;
                // Freeze the recording before the snapshot, so nothing lands in the gap and is lost with the journal. It stays
                // "recording" until the save finishes, so nothing can start a new recording over it.
                _stopping = true;
                filePath = _filePath;
                metadata = _metadata with { EndedAt = DateTimeOffset.UtcNow };
            }
            try
            {
                var windows = await CollectAsync(evidence, metadata, cancellationToken).ConfigureAwait(false);
                SessionDocument document;
                lock (_gate) document = BuildDocument(metadata) with { WindowsEvidence = windows };
                await SaveAtomicAsync(document, filePath, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // The recording (and its journal) stays live so Stop can be retried. This also covers a cancelled evidence read.
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
                _previous = null;
                _machine = null;
                _strings.Clear();
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
                document = BuildDocument(_metadata with { EndedAt = DateTimeOffset.UtcNow });
                filePath = _filePath;
            }
            await SaveAtomicAsync(document, filePath, cancellationToken).ConfigureAwait(false);
            return filePath;
        }
        finally { _writeGate.Release(); }
    }

    // Callers hold _gate.
    private SessionDocument BuildDocument(SessionMetadata metadata)
        => new(metadata, _samples.ToArray(), _events.OrderBy(item => item.Timestamp).ToArray()) { Machine = _machine, Traces = _traces.ToArray() };

    private static async Task<SessionWindowsEvidence?> CollectAsync(SessionEvidenceSource? evidence, SessionMetadata metadata, CancellationToken cancellationToken)
    {
        if (evidence is null) return null;
        try { return await evidence(metadata, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException) { return null; }
    }

    private static async Task SaveAtomicAsync(SessionDocument document, string filePath, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = filePath + ".writing";
        try
        {
            await using (var file = File.Create(temporary))
            await using (var compressed = SessionFileFormat.Compress(file))
                await JsonSerializer.SerializeAsync(compressed, document, JsonOptions, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, filePath, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    // Writes a document produced elsewhere (for example a redacted export) in the session file format.
    public static Task SaveAsync(SessionDocument document, string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        return SaveAtomicAsync(document, filePath, cancellationToken);
    }

    public static async Task WriteAsync(SessionDocument document, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(destination);
        await using var compressed = SessionFileFormat.Compress(destination, leaveOpen: true);
        await JsonSerializer.SerializeAsync(compressed, document, JsonOptions, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<SessionDocument> LoadAsync(string filePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        await using var file = File.OpenRead(filePath);
        await using var decompressed = SessionFileFormat.Decompress(file);
        var document = await JsonSerializer.DeserializeAsync<SessionDocument>(decompressed, JsonOptions, cancellationToken);
        return document ?? throw new InvalidDataException("The session file did not contain a session document.");
    }

    // Callers hold _gate.
    private void AddEvent(SessionEvent item)
    {
        _events.Add(item);
        _journal?.WriteEvent(item);
    }

    // Callers hold _gate.
    private SystemMetricsSnapshot Compact(SystemMetricsSnapshot snapshot)
    {
        var changed = false;
        var processes = new ProcessMetric[snapshot.Processes.Count];
        for (var index = 0; index < processes.Length; index++)
        {
            var process = snapshot.Processes[index];
            var name = Share(process.Name)!;
            var path = Share(process.ExecutablePath);
            var user = Share(process.UserName);
            if (ReferenceEquals(name, process.Name) && ReferenceEquals(path, process.ExecutablePath) && ReferenceEquals(user, process.UserName))
                processes[index] = process;
            else
            {
                processes[index] = process with { Name = name, ExecutablePath = path, UserName = user };
                changed = true;
            }
        }
        return changed ? snapshot with { Processes = processes } : snapshot;

        string? Share(string? value)
        {
            if (value is null) return null;
            if (_strings.TryGetValue(value, out var shared)) return shared;
            _strings[value] = value;
            return value;
        }
    }

    // Callers hold _gate. Threshold conditions are logged once when they start and once when they end, never on every sample.
    private void AddAutomaticEvents(SystemMetricsSnapshot snapshot)
    {
        var previous = _previous;
        _previous = snapshot;
        var gap = previous is null || (snapshot.Timestamp - previous.Timestamp).TotalSeconds > 6;
        Condition(SessionEventType.CpuSaturation, snapshot.Cpu.Availability.IsSupported && snapshot.Cpu.UtilizationPercent >= 90,
            "CPU above 90%", $"CPU {snapshot.Cpu.UtilizationPercent:F1}%");
        Condition(SessionEventType.MemoryPressure, snapshot.Memory.Availability.IsSupported && snapshot.Memory.UsedPercent >= 90 && snapshot.Memory.AvailableBytes <= 1024UL * 1024 * 1024,
            "Low available memory", $"{snapshot.Memory.UsedPercent:F1}% occupied; paging pressure not assessed");
        Condition(SessionEventType.DiskActivity, snapshot.Disk.Availability.IsSupported && snapshot.Disk.ActiveTimePercent >= 90,
            "Disk active above 90%", $"Disk active {snapshot.Disk.ActiveTimePercent:F1}%");
        if (previous is null) return;

        var seconds = (snapshot.Timestamp - previous.Timestamp).TotalSeconds;
        // An empty process list means the sample failed to read processes, not that every process exited.
        if (previous.Processes.Count > 0 && snapshot.Processes.Count > 0)
        {
            var before = previous.Processes.DistinctBy(ProcessKey).ToDictionary(ProcessKey);
            var now = snapshot.Processes.DistinctBy(ProcessKey).ToDictionary(ProcessKey);
            foreach (var process in now.Where(item => !before.ContainsKey(item.Key)).Select(item => item.Value))
                AddEvent(new SessionEvent(snapshot.Timestamp, SessionEventType.ProcessLaunch,
                    $"{process.Name} (PID {process.ProcessId}) started{(process.HasVisibleWindow ? " · has a window" : string.Empty)}")
                    { ProcessName = process.Name, ProcessId = process.ProcessId, HadWindow = process.HasVisibleWindow });
            foreach (var process in before.Where(item => !now.ContainsKey(item.Key)).Select(item => item.Value))
                AddEvent(new SessionEvent(snapshot.Timestamp, SessionEventType.ProcessExit,
                    $"{process.Name} (PID {process.ProcessId}) exited · last seen {seconds:F0} s earlier{(process.HasVisibleWindow ? " · had a window" : string.Empty)}"
                    + (process.TotalCpuTime is { } cpu ? $" · CPU time {(int)cpu.TotalHours:00}:{cpu.Minutes:00}:{cpu.Seconds:00}" : string.Empty))
                    { ProcessName = process.Name, ProcessId = process.ProcessId, HadWindow = process.HasVisibleWindow });
        }
        if (previous.Power.Source != snapshot.Power.Source && previous.Power.Source != PowerSource.Unknown && snapshot.Power.Source != PowerSource.Unknown)
            AddEvent(new SessionEvent(snapshot.Timestamp, SessionEventType.PowerSourceChanged, $"Power source changed from {previous.Power.Source} to {snapshot.Power.Source}"));
        if (previous.Network.Availability.IsSupported && snapshot.Network.Availability.IsSupported)
        {
            var before = previous.Network.Interfaces.DistinctBy(item => item.Id).ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
            var now = snapshot.Network.Interfaces.DistinctBy(item => item.Id).ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
            foreach (var adapter in now.Values.Where(item => !before.ContainsKey(item.Id)))
                AddEvent(new SessionEvent(snapshot.Timestamp, SessionEventType.NetworkChanged, $"Network adapter became active: {adapter.Name} ({adapter.Kind})"));
            foreach (var adapter in before.Values.Where(item => !now.ContainsKey(item.Id)))
                AddEvent(new SessionEvent(snapshot.Timestamp, SessionEventType.NetworkChanged, $"Network adapter no longer active: {adapter.Name} ({adapter.Kind})"));
        }

        void Condition(SessionEventType type, bool active, string label, string detail)
        {
            if (_activeConditions.TryGetValue(type, out var since) && (gap || !active))
            {
                var endedAt = gap && previous is not null ? previous.Timestamp : snapshot.Timestamp;
                AddEvent(new SessionEvent(endedAt, type, $"{label} ended after {Math.Max(0, (endedAt - since).TotalSeconds):F0} s{(gap ? " (samples interrupted)" : string.Empty)}"));
                _activeConditions.Remove(type);
            }
            if (active && !_activeConditions.ContainsKey(type))
            {
                _activeConditions[type] = snapshot.Timestamp;
                AddEvent(new SessionEvent(snapshot.Timestamp, type, $"{label} · {detail}"));
            }
        }
    }

    private static string ProcessKey(ProcessMetric process)
        => $"{process.ProcessId}|{process.StartTime?.UtcTicks ?? 0}|{process.Name}";

    // Journals left behind by a recording that never reached Stop (app crash, freeze, power loss, forced restart).
    public static IReadOnlyList<string> FindInterruptedJournals(string directory)
        => Directory.Exists(directory) ? Directory.EnumerateFiles(directory, "*" + SessionJournal.Extension).Where(path => path.EndsWith(".perfsession" + SessionJournal.Extension, StringComparison.OrdinalIgnoreCase)).ToArray() : [];

    // Rebuilds a .perfsession from a journal and removes the journal.
    // Returns null when nothing new was recovered: the journal is in use, holds nothing usable (it is then removed), or the
    // session file next to it is already at least as complete (a Stop whose journal cleanup failed, or a checkpoint).
    public static async Task<string?> RecoverAsync(string journalPath, SessionEvidenceSource? evidence = null, CancellationToken cancellationToken = default)
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
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or JsonException) { }
        }
        if (document is not null)
        {
            document = document with { WindowsEvidence = await CollectAsync(evidence, document.Metadata, cancellationToken).ConfigureAwait(false) };
            await SaveAtomicAsync(document, target, cancellationToken).ConfigureAwait(false);
        }
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
