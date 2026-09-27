using System.IO.Compression;
using Skald.Recorder;
using Xunit;

namespace Skald.Core.Tests;

public sealed class SessionArchiveTests
{
    [Fact]
    public async Task ZipContainsEverySelectedSessionAndPreservesSourceFolder()
    {
        var root = Path.Combine(Path.GetTempPath(), $"skald-archive-{Guid.NewGuid():N}");
        var firstDirectory = Path.Combine(root, "Skald Sessions");
        var secondDirectory = Path.Combine(root, "Older Sessions");
        Directory.CreateDirectory(firstDirectory);
        Directory.CreateDirectory(secondDirectory);
        var first = Path.Combine(firstDirectory, "same.perfsession");
        var second = Path.Combine(secondDirectory, "same.perfsession");
        var destination = Path.Combine(root, "exports", "sessions.zip");
        try
        {
            await File.WriteAllTextAsync(first, "new");
            await File.WriteAllTextAsync(second, "old");
            await SessionArchive.CreateAsync([first, second], destination);
            using var zip = ZipFile.OpenRead(destination);
            Assert.Equal(2, zip.Entries.Count);
            Assert.Contains(zip.Entries, entry => entry.FullName == "Skald Sessions/same.perfsession");
            Assert.Contains(zip.Entries, entry => entry.FullName == "Older Sessions/same.perfsession");
        }
        finally
        {
            if (File.Exists(first)) File.Delete(first);
            if (File.Exists(second)) File.Delete(second);
            if (File.Exists(destination)) File.Delete(destination);
            var exportDirectory = Path.GetDirectoryName(destination)!;
            if (Directory.Exists(exportDirectory)) Directory.Delete(exportDirectory);
            if (Directory.Exists(firstDirectory)) Directory.Delete(firstDirectory);
            if (Directory.Exists(secondDirectory)) Directory.Delete(secondDirectory);
            if (Directory.Exists(root)) Directory.Delete(root);
        }
    }
}
