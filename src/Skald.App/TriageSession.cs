using Skald.Triage;

namespace Skald.App;

// The most recent hardware check, shared so the Home page can show it and build the hand-off without re-running the scan.
public static class TriageSession
{
    public static TriageResult? Last { get; private set; }

    public static event Action? Changed;

    public static void Set(TriageResult result)
    {
        Last = result;
        Changed?.Invoke();
    }
}
