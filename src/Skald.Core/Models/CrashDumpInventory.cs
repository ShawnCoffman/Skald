namespace Skald.Core.Models;

public sealed record CrashDumpFile(string Kind, string Path, DateTimeOffset ModifiedAt, long SizeBytes);
public sealed record CrashDumpInventory(DateTimeOffset CollectedAt, IReadOnlyList<CrashDumpFile> Files,
    IReadOnlyList<string> SourceStatus);
