using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Skald.Core.Models;

namespace Skald.Recorder;

// An append-only crash-safe copy of a recording in progress: one Brotli stream of JSON lines. A background task flushes the stream
// and the file after every entry, so everything before the last flush can be decoded and a freeze, bugcheck or power loss costs at
// most the last sample or two. Journals from earlier versions (one gzip member per entry) are still read.
internal sealed class SessionJournal : IAsyncDisposable
{
    public const string Extension = ".journal";

    private readonly Channel<Func<byte[]>> _entries = Channel.CreateUnbounded<Func<byte[]>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly FileStream _file;
    private readonly Stream _compressor;
    private readonly Task _pump;
    private readonly JsonSerializerOptions _options;

    public SessionJournal(string path, JsonSerializerOptions options)
    {
        Path = path;
        _options = options;
        var directory = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        // Readers may look but not write, which lets recovery tell a live journal from an abandoned one.
        _file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, FileOptions.None);
        _compressor = SessionFileFormat.Compress(_file, leaveOpen: true);
        _pump = Task.Run(PumpAsync);
    }

    public string Path { get; }

    public bool Failed { get; private set; }

    public void WriteMetadata(SessionMetadata metadata) => Enqueue("m", metadata);
    public void WriteSample(SystemMetricsSnapshot sample) => Enqueue("s", sample);
    public void WriteEvent(SessionEvent item) => Enqueue("e", item);
    public void WriteMachine(SessionMachine machine) => Enqueue("h", machine);
    public void WriteTrace(SessionTrace trace) => Enqueue("t", trace);

    private void Enqueue<T>(string kind, T payload)
        => _entries.Writer.TryWrite(() => JsonSerializer.SerializeToUtf8Bytes(new JournalLine<T>(kind, payload), _options));

    private async Task PumpAsync()
    {
        await foreach (var serialize in _entries.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (Failed) continue;
            try
            {
                _compressor.Write(serialize());
                _compressor.WriteByte((byte)'\n');
                await _compressor.FlushAsync().ConfigureAwait(false);
                _file.Flush(flushToDisk: true);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The recording itself continues in memory and is saved on Stop; only crash protection is lost.
                Failed = true;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _entries.Writer.TryComplete();
        await _pump.ConfigureAwait(false);
        // Closing flushes any bytes a failed write left buffered; that failure only affects crash protection, never the session.
        try { await _compressor.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException) { Failed = true; }
        try { await _file.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Failed = true; }
    }

    public async Task DeleteAsync()
    {
        await DisposeAsync().ConfigureAwait(false);
        await TryDeleteAsync(Path).ConfigureAwait(false);
    }

    // Antivirus and OneDrive can hold a just-closed file briefly; a journal left behind would be "recovered" over the saved session.
    public static async Task TryDeleteAsync(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try { File.Delete(path); return; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { await Task.Delay(200).ConfigureAwait(false); }
        }
    }

    public static string PathFor(string sessionPath) => sessionPath + Extension;

    private sealed record JournalLine<T>(string K, T D);

    // Reads whatever a journal holds, tolerating a truncated final entry. Document is null when nothing usable (no metadata) was
    // found; InUse is true when a recorder still has the journal open for writing.
    public static async Task<(SessionDocument? Document, bool InUse)> ReadAsync(string path, JsonSerializerOptions options, CancellationToken cancellationToken)
    {
        FileStream file;
        try { file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None); }
        catch (FileNotFoundException) { return (null, false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return (null, true); }
        await using (file.ConfigureAwait(false))
        {
            SessionMetadata? metadata = null;
            SessionMachine? machine = null;
            var samples = new List<SystemMetricsSnapshot>();
            var events = new List<SessionEvent>();
            var traces = new List<SessionTrace>();
            using var reader = new StreamReader(SessionFileFormat.Decompress(file), Encoding.UTF8);
            try
            {
                while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
                {
                    try
                    {
                        using var document = JsonDocument.Parse(line);
                        var data = document.RootElement.GetProperty("d");
                        switch (document.RootElement.GetProperty("k").GetString())
                        {
                            case "m": metadata = data.Deserialize<SessionMetadata>(options); break;
                            case "s": if (data.Deserialize<SystemMetricsSnapshot>(options) is { } sample) samples.Add(sample); break;
                            case "e": if (data.Deserialize<SessionEvent>(options) is { } item) events.Add(item); break;
                            case "h": machine = data.Deserialize<SessionMachine>(options); break;
                            case "t": if (data.Deserialize<SessionTrace>(options) is { } trace) traces.Add(trace); break;
                        }
                    }
                    catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException) { }
                }
            }
            // A torn or corrupt tail ends the read; everything decoded before it is kept, as with a truncated file. Brotli reports
            // corrupt data as InvalidOperationException, gzip as InvalidDataException.
            catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or EndOfStreamException or IOException) { }
            if (metadata is null) return (null, false);
            var ordered = samples.OrderBy(sample => sample.Timestamp).DistinctBy(sample => sample.Timestamp).ToArray();
            return (new SessionDocument(metadata with { EndedAt = ordered.Length > 0 ? ordered[^1].Timestamp : metadata.StartedAt, Recovered = true },
                ordered, events.OrderBy(item => item.Timestamp).ToArray()) { Machine = machine, Traces = traces }, false);
        }
    }
}
