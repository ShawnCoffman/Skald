using Skald.Core.Models;
using Xunit;

namespace Skald.Core.Tests;

public sealed class StorageIncidentAnalysisTests
{
    [Theory]
    [InlineData("The IO operation at logical block address 123 for Disk 2 was retried.", 2u)]
    [InlineData(@"An error was detected on device \Device\Harddisk4\DR4 during a paging operation.", 4u)]
    [InlineData("Disk 5 has been surprise removed.", 5u)]
    public void LinksOnlyExplicitDiskIdentifiers(string detail, uint diskNumber)
    {
        var link = StorageIncidentAnalysis.Link(Event("disk", 153, detail));
        Assert.NotNull(link);
        Assert.Equal(diskNumber, link.DiskNumber);
        Assert.Equal(HardwareMatchConfidence.Probable, link.Confidence);
    }

    [Fact]
    public void ControllerPortIsNotMistakenForDiskNumber()
    {
        var link = StorageIncidentAnalysis.Link(Event("storport", 129, @"Reset to device, \Device\RaidPort2, was issued."));
        Assert.NotNull(link);
        Assert.Null(link.DiskNumber);
        Assert.Equal(HardwareMatchConfidence.Unmapped, link.Confidence);
    }

    [Fact]
    public void ConflictingDiskNumbersStayUnmapped()
    {
        var link = StorageIncidentAnalysis.Link(Event("disk", 153, "Retry for Disk 2 and then for Disk 3."));
        Assert.NotNull(link);
        Assert.Null(link.DiskNumber);
    }

    private static ReliabilityEvent Event(string provider, int id, string details)
        => new(DateTimeOffset.UtcNow, "System", 1, provider, id, "Drivers & storage", "Warning", provider, details, details, "");
}
