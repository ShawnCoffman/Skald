using Skald.Core.Models;
using Xunit;

namespace Skald.Core.Tests;

public sealed class CrashTriageTests
{
    [Fact]
    public void ExtractsStructuredWheaFieldsWithoutGuessingDevice()
    {
        var time = DateTimeOffset.UtcNow;
        var report = new ReliabilityEvent(time, "System", 42, "Microsoft-Windows-WHEA-Logger", 17,
            "Hardware", "Warning", "WHEA", "Corrected error", "Corrected error",
            "<Event><EventData><Data Name=\"ErrorSource\">4</Data><Data Name=\"RawData\">AABB</Data></EventData></Event>");
        var result = CrashTriage.Describe(report, []);
        Assert.Contains("ErrorSource=4", result);
        Assert.DoesNotContain("RawData", result);
        Assert.Contains("not necessarily the replaceable failing part", result);
    }
}
