using GmapPlanner.Core.Models;
using GmapPlanner.Core.Services;
using GmapPlanner.Core.Services.Publish;
using Xunit;

namespace GmapPlanner.Core.Tests;

public class PublishHelpersTests
{
    [Theory]
    [InlineData("1-10.kml", "טיול", "טיול (ימים 1-10)")]
    [InlineData("1.kml", "טיול", "טיול (יום 1)")]
    [InlineData("11-12.kml", "", "ימים 11-12")]
    [InlineData("3.kml", "", "יום 3")]
    public void TitleFor_DerivesTheDaySpanFromTheFileName(string file, string trip, string expected)
    {
        Assert.Equal(expected, PublishService.TitleFor(file, trip));
    }

    [Theory]
    [InlineData("viewer", "reader")]
    [InlineData("reader", "reader")]
    [InlineData("commenter", "commenter")]
    [InlineData("comment", "commenter")]
    [InlineData("editor", "writer")]
    [InlineData("writer", "writer")]
    [InlineData("EDITOR", "writer")]
    [InlineData("  viewer  ", "reader")]
    [InlineData(null, "reader")]
    [InlineData("", "reader")]
    public void NormalizeRole_MapsFriendlyRolesToDriveRoles(string? role, string expected)
    {
        Assert.Equal(expected, DriveShareService.NormalizeRole(role));
    }

    [Fact]
    public void NormalizeRole_UnknownRole_Throws()
    {
        var ex = Assert.Throws<DriveShareException>(() => DriveShareService.NormalizeRole("owner"));
        Assert.Contains("Unknown share role", ex.Message);
    }

    [Fact]
    public void ViewUrl_PrefersTheMidBasedViewerLink()
    {
        var withMid = new PublishedMap { File = "1.kml", Title = "t", Url = "https://edit", Mid = "abc123" };
        var withoutMid = new PublishedMap { File = "1.kml", Title = "t", Url = "https://edit" };

        Assert.Equal("https://www.google.com/maps/d/viewer?mid=abc123", withMid.ViewUrl);
        Assert.Equal("https://edit", withoutMid.ViewUrl);
    }

    /// <summary>
    /// The import check reads the first placemark name back out of the KML we wrote, so
    /// the two have to agree — this walks a real generated file rather than a fixture.
    /// </summary>
    [Fact]
    public void FirstPlacemarkName_ReadsTheNameBackFromAGeneratedKml()
    {
        var days = new List<Day>
        {
            new()
            {
                DayNumber = 1,
                Locations = [new Location { Name = "Nishiki Market, Kyoto", Lat = 35.005, Lng = 135.765 }],
            },
        };
        var dir = Path.Combine(Path.GetTempPath(), "gmap-planner-publish-" + Guid.NewGuid());
        try
        {
            var files = KmlBuilder.WriteKmlFiles(KmlBuilder.ChunkDays(days, 10), dir);

            Assert.Equal("Nishiki Market, Kyoto", MyMapsSession.FirstPlacemarkName(files[0]));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void FirstPlacemarkName_UnreadableFile_ReturnsNull()
    {
        Assert.Null(MyMapsSession.FirstPlacemarkName(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid())));
    }
}
