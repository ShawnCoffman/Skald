using System.IO.Compression;

namespace Skald.Recorder;

public static class SessionArchive
{
    public static Task CreateAsync(IReadOnlyList<string> sessionPaths, string destination, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sessionPaths);
        if (sessionPaths.Count == 0) throw new ArgumentException("Select at least one session.", nameof(sessionPaths));
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        return Task.Run(() => Create(sessionPaths, destination, cancellationToken), cancellationToken);
    }

    private static void Create(IReadOnlyList<string> sessionPaths, string destination, CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        Directory.CreateDirectory(directory);
        var temporary = destination + ".writing";
        try
        {
            using (var output = File.Create(temporary))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
            {
                foreach (var path in sessionPaths.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!Path.GetExtension(path).Equals(".perfsession", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Not a Skald session: {path}");
                    var parent = Path.GetFileName(Path.GetDirectoryName(path));
                    var entryName = $"{parent}/{Path.GetFileName(path)}";
                    zip.CreateEntryFromFile(path, entryName, CompressionLevel.NoCompression);
                }
            }
            File.Move(temporary, destination, overwrite: false);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
