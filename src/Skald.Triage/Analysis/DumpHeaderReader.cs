using System.Buffers.Binary;

namespace Skald.Triage;

public sealed record DumpHeaderInfo(uint BugcheckCode, IReadOnlyList<ulong> Parameters, uint ProcessorCount)
{
    public BugcheckInfo Bugcheck => Bugchecks.Describe(BugcheckCode);
}

// Reads only the fixed DUMP_HEADER64 prefix of a kernel dump. It does not walk memory or name the faulting driver.
public static class DumpHeaderReader
{
    private const int HeaderLength = 0x60;

    public static DumpHeaderInfo? TryRead(string path)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[HeaderLength];
            var read = file.ReadAtLeast(buffer, HeaderLength, throwOnEndOfStream: false);
            return TryParse(buffer.AsSpan(0, read));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return null; }
    }

    public static DumpHeaderInfo? TryParse(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderLength) return null;
        // "PAGE" + "DU64" marks a 64-bit kernel dump; user-mode "MDMP" files and 32-bit dumps are not handled.
        if (!header[..8].SequenceEqual("PAGEDU64"u8)) return null;
        var processors = BinaryPrimitives.ReadUInt32LittleEndian(header[0x34..]);
        var code = BinaryPrimitives.ReadUInt32LittleEndian(header[0x38..]);
        var parameters = new ulong[4];
        for (var i = 0; i < parameters.Length; i++)
            parameters[i] = BinaryPrimitives.ReadUInt64LittleEndian(header[(0x40 + 8 * i)..]);
        return new DumpHeaderInfo(code, parameters, processors);
    }
}
