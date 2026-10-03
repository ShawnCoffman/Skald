using System.Globalization;

namespace Skald.Triage;

public static class DriverComparison
{
    public static IReadOnlyList<DriverChange> Compare(IReadOnlyList<DriverRecord> before, IReadOnlyList<DriverRecord> after)
    {
        var old = Index(before);
        var current = Index(after);
        var changes = new List<DriverChange>();
        foreach (var (key, now) in current)
        {
            if (!old.TryGetValue(key, out var was)) { changes.Add(new(DriverChangeKind.Added, null, now)); continue; }
            if (string.Equals(was.Version, now.Version, StringComparison.OrdinalIgnoreCase)
                && string.Equals(was.Provider, now.Provider, StringComparison.OrdinalIgnoreCase)) continue;
            var kind = Version.TryParse(was.Version, out var oldVersion) && Version.TryParse(now.Version, out var newVersion)
                ? newVersion > oldVersion ? DriverChangeKind.Updated : newVersion < oldVersion ? DriverChangeKind.Downgraded : DriverChangeKind.Changed
                : DriverChangeKind.Changed;
            changes.Add(new(kind, was, now));
        }
        changes.AddRange(old.Where(item => !current.ContainsKey(item.Key)).Select(item => new DriverChange(DriverChangeKind.Removed, item.Value, null)));
        // Vendor drivers first: inbox Microsoft drivers churn with every cumulative update.
        return changes.OrderBy(change => IsInbox(change.Subject) ? 1 : 0).ThenBy(change => change.Subject.Class, StringComparer.OrdinalIgnoreCase)
            .ThenBy(change => change.Subject.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static bool IsInbox(DriverRecord driver) => driver.Provider.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase);

    private static Dictionary<string, DriverRecord> Index(IReadOnlyList<DriverRecord> drivers) => drivers
        .Where(driver => !string.IsNullOrWhiteSpace(driver.DeviceId) && !string.IsNullOrWhiteSpace(driver.Version))
        .GroupBy(driver => driver.DeviceId, StringComparer.OrdinalIgnoreCase)
        .ToDictionary(group => group.Key, group => group.OrderByDescending(driver => driver.Date ?? DateTimeOffset.MinValue).First(), StringComparer.OrdinalIgnoreCase);

    // Win32_PnPSignedDriver reports dates as CIM datetime strings ("20230615000000.******+***").
    public static DateTimeOffset? ParseCimDate(string? value)
        => value is { Length: >= 8 } && DateTime.TryParseExact(value[..8], "yyyyMMdd", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? new DateTimeOffset(date, TimeSpan.Zero) : null;
}
