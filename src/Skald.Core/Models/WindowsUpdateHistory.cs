namespace Skald.Core.Models;

public sealed record WindowsUpdateEntry(DateTimeOffset Date, string Title, string Operation, string Result, string? HResult);
public sealed record WindowsUpdateHistory(DateTimeOffset CollectedAt, IReadOnlyList<WindowsUpdateEntry> Entries,
    int? PendingCount, DateTimeOffset? LastSuccessfulSearch, IReadOnlyList<string> SourceStatus);
