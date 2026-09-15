using GmapPlanner.Core.Models;
using GmapPlanner.Core.Services;
using Xunit;

namespace GmapPlanner.Core.Tests;

public class KmlBuilderTests
{
    [Theory]
    [InlineData("Tokyo/Kyoto Trip", "TokyoKyoto Trip")]
    [InlineData("Trip: Japan?", "Trip Japan")]
    [InlineData("   ", "Trip")]
    [InlineData("Valid Name.", "Valid Name")]
    public void SanitizeFolderName_StripsInvalidCharsAndTrailingDotsSpaces(string input, string expected)
    {
        Assert.Equal(expected, KmlBuilder.SanitizeFolderName(input));
    }

    [Theory]
    [InlineData(1, 20)]
    [InlineData(9, 20)]
    [InlineData(10, 17)]
    [InlineData(99, 17)]
    [InlineData(100, 12)]
    public void NumberedPinHref_ShrinksPsizeAsDigitsGrow(int n, int expectedPsize)
    {
        var href = KmlBuilder.NumberedPinHref(n, "D32F2F");
        Assert.Contains($"psize={expectedPsize}", href);
        Assert.Contains($"text={n}", href);
        Assert.Contains("highlight=D32F2F,D32F2F,ffffff", href);
    }

    [Fact]
    public void ChunkDays_SplitsIntoSizeCappedAtMaxLayersPerFile()
    {
        var days = Enumerable.Range(1, 25).Select(n => new Day { DayNumber = n }).ToList();

        var chunks = KmlBuilder.ChunkDays(days, layersPerFile: 100);

        Assert.All(chunks, c => Assert.True(c.Count <= 10));
        Assert.Equal(25, chunks.Sum(c => c.Count));
    }

    [Fact]
    public void WriteKmlFiles_WritesOneFilePerChunkWithPlacemarksAndDayColors()
    {
        var days = new List<Day>
        {
            new()
            {
                DayNumber = 1,
                Date = "15/06",
                Locations =
                [
                    new Location { Name = "Nishiki Market, Kyoto", Lat = 35.005, Lng = 135.765, Notes = "שוק" },
                    new Location { Name = "Fushimi Inari", Lat = 34.967, Lng = 135.772 },
                ],
            },
        };
        var chunks = KmlBuilder.ChunkDays(days, layersPerFile: 10);
        var outputDir = Path.Combine(Path.GetTempPath(), "gmap-planner-tests-" + Guid.NewGuid());

        try
        {
            var paths = KmlBuilder.WriteKmlFiles(chunks, outputDir);

            var path = Assert.Single(paths);
            Assert.Equal("1.kml", Path.GetFileName(path));
            var xml = File.ReadAllText(path);
            Assert.Contains("Nishiki Market, Kyoto", xml);
            Assert.Contains("0288D1", xml); // day 1 color
            Assert.Contains("Day 1 (15/06)", xml);

            var coordinates = System.Xml.Linq.XDocument.Parse(xml)
                .Descendants().First(e => e.Name.LocalName == "coordinates").Value;
            var parts = coordinates.Split(',');
            Assert.Equal(135.765, double.Parse(parts[0]), precision: 6); // lng first
            Assert.Equal(35.005, double.Parse(parts[1]), precision: 6); // then lat
        }
        finally
        {
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    [Fact]
    public void BuildKmlFiles_NamesByDayRangeAndHoldsXmlInMemory()
    {
        var days = Enumerable.Range(1, 12)
            .Select(n => new Day
            {
                DayNumber = n,
                Locations = [new Location { Name = $"Place {n}", Lat = 35.0, Lng = 139.0 }],
            })
            .ToList();

        var files = KmlBuilder.BuildKmlFiles(KmlBuilder.ChunkDays(days, layersPerFile: 10));

        Assert.Equal(new[] { "1-10.kml", "11-12.kml" }, files.Select(f => f.FileName));
        Assert.StartsWith("<?xml", files[0].Content);
        Assert.Contains("utf-8", files[0].Content.Split('\n')[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Place 10", files[0].Content);
        Assert.DoesNotContain("Place 11", files[0].Content);
        Assert.Contains("Place 12", files[1].Content);
    }

    [Fact]
    public void WriteKmlFiles_WritesExactlyTheInMemoryContent()
    {
        var days = new List<Day>
        {
            new() { DayNumber = 1, Locations = [new Location { Name = "שוק נישיקי, Kyoto", Lat = 35.005, Lng = 135.765 }] },
        };
        var chunks = KmlBuilder.ChunkDays(days, layersPerFile: 10);
        var outputDir = Path.Combine(Path.GetTempPath(), "gmap-planner-tests-" + Guid.NewGuid());

        try
        {
            var expected = Assert.Single(KmlBuilder.BuildKmlFiles(chunks));
            var path = Assert.Single(KmlBuilder.WriteKmlFiles(chunks, outputDir));

            Assert.Equal(expected.FileName, Path.GetFileName(path));
            Assert.Equal(new System.Text.UTF8Encoding(false).GetBytes(expected.Content), File.ReadAllBytes(path));
        }
        finally
        {
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }
}
