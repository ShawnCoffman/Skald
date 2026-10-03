using System.IO.Compression;

namespace Skald.Recorder;

public sealed record SessionArchiveOptions
{
    // Adds each recording's deep traces (ETL). Ignored when Redact is set: a trace cannot be scrubbed.
    public bool IncludeTraces { get; init; }
    // Writes each recording through SessionRedactor instead of copying the file.
    public bool Redact { get; init; }
}

public static class SessionArchive
{
    public static Task CreateAsync(IReadOnlyList<string> sessionPaths, string destination, CancellationToken cancellationToken = default)
        => CreateAsync(sessionPaths, destination, new SessionArchiveOptions(), cancellationToken);

    public static Task CreateAsync(IReadOnlyList<string> sessionPaths, string destination, SessionArchiveOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionPaths);
        ArgumentNullException.ThrowIfNull(options);
        if (sessionPaths.Count == 0) throw new ArgumentException("Select at least one session.", nameof(sessionPaths));
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        return Task.Run(() => CreateCoreAsync(sessionPaths, destination, options, cancellationToken), cancellationToken);
    }

    private static async Task CreateCoreAsync(IReadOnlyList<string> sessionPaths, string destination, SessionArchiveOptions options, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        Directory.CreateDirectory(directory);
        var temporary = destination + ".writing";
        try
        {
            await using (var output = File.Create(temporary))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
            {
                foreach (var path in sessionPaths.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Path.GetExtension(path).Equals(".perfsession", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Not a Skald session: {path}");
                    var parent = Path.GetFileName(Path.GetDirectoryName(path));
                    var entryName = $"{parent}/{Path.GetFileName(path)}";
                    if (options.Redact)
                    {
                        var redacted = SessionRedactor.Redact(await FlightRecorder.LoadAsync(path, cancellationToken).ConfigureAwait(false));
                        await using var entry = zip.CreateEntry(entryName, CompressionLevel.NoCompression).Open();
                        await FlightRecorder.WriteAsync(redacted, entry, cancellationToken).ConfigureAwait(false);
                        continue;
                    }
                    zip.CreateEntryFromFile(path, entryName, CompressionLevel.NoCompression);
                    if (!options.IncludeTraces) continue;
                    SessionDocument document;
                    try { document = await FlightRecorder.LoadAsync(path, cancellationToken).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or System.Text.Json.JsonException) { continue; }
                    foreach (var trace in document.Traces)
                    {
                        var tracePath = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileName(trace.FileName));
                        if (File.Exists(tracePath)) zip.CreateEntryFromFile(tracePath, $"{parent}/{Path.GetFileName(tracePath)}", CompressionLevel.Optimal);
                    }
                }
            }
            File.Move(temporary, destination, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
