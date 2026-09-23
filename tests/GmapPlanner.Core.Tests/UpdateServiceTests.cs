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

    [Theory]
    [InlineData("/Applications/My Maps Generator.app/Contents/MacOS/", "/Applications/My Maps Generator.app")]
    [InlineData("/Users/me/Apps/My Maps Generator.app/Contents/MacOS", "/Users/me/Apps/My Maps Generator.app")]
    [InlineData("/Users/me/src/GmapPlanner.App/bin/Debug/net8.0/osx-arm64/", null)]
    public void MacBundlePath_findsTheAppBundle(string baseDir, string? expected) =>
        Assert.Equal(expected, UpdateService.MacBundlePath(baseDir));

    [Fact]
    public void MacSwapScript_waitsSwapsAndRelaunches()
    {
        var s = UpdateService.MacSwapScript("/tmp/it's.dmg", "/Applications/My Maps Generator.app", 4242);

        Assert.Contains("while kill -0 4242", s);
        // Paths are single-quoted, with embedded quotes escaped, so spaces/quotes are safe.
        Assert.Contains("'/tmp/it'\\''s.dmg'", s);
        Assert.Contains("'/Applications/My Maps Generator.app'", s);
        Assert.Contains("hdiutil attach", s);
        Assert.Contains("ditto", s);
        Assert.Contains("hdiutil detach", s);
        Assert.Contains("open ", s);
        // Replace = delete the old bundle first, so files the new version dropped don't linger.
        Assert.True(s.IndexOf("rm -rf", StringComparison.Ordinal) < s.IndexOf("ditto", StringComparison.Ordinal));
    }
}
