using GmapPlanner.Core.Services;
using Xunit;

namespace GmapPlanner.Core.Tests;

/// <summary>
/// Ports the version-comparison logic from the Python original's updater._is_newer.
/// This is the one decision that gates a self-update, so a wrong compare either nags
/// forever or silently never updates.
/// </summary>
public class UpdateServiceTests
{
    [Theory]
    [InlineData("1.0.1", "1.0.0", true)]
    [InlineData("1.1.0", "1.0.9", true)]
    [InlineData("2.0.0", "1.9.9", true)]
    [InlineData("v1.2.0", "1.1.0", true)]   // leading v tolerated
    [InlineData("1.0.0", "1.0.0", false)]   // equal is not newer
    [InlineData("1.0.0", "1.0.1", false)]   // older is not newer
    [InlineData("1.0", "1.0.0", false)]     // missing parts pad to 0
    [InlineData("1.0.0", "1.0", false)]
    [InlineData("1.2.0", "1.2", false)]     // 1.2.0 == 1.2 (padded), not newer
    public void IsNewer_comparesSemverParts(string latest, string current, bool expected)
    {
        Assert.Equal(expected, UpdateService.IsNewer(latest, current));
    }
}
