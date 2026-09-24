using GmapPlanner.Core.Services;

namespace GmapPlanner.Core.Tests;

public class PublishProgressTests
{
    [Fact]
    public void Parse_roundTripsEveryPerMapLine()
    {
        Assert.Equal((1, MapStep.Creating), PublishProgress.Parse(PublishProgress.Creating(1, 3, "טיול ביפן (ימים 4-6)")));
        Assert.Equal((0, MapStep.Sharing), PublishProgress.Parse(PublishProgress.Sharing(0, 3)));
        Assert.Equal((2, MapStep.Created), PublishProgress.Parse(PublishProgress.Created(2, 3)));
        Assert.Equal((2, MapStep.Failed), PublishProgress.Parse(PublishProgress.Failed(2, 3)));
    }

    [Theory]
    [InlineData("Done")]
    [InlineData("Submitting the publish job…")]
    [InlineData("Map 2/3")]
    public void Parse_ignoresOtherLines(string line) => Assert.Null(PublishProgress.Parse(line));
}
