using GmapPlanner.Core.Services;

namespace GmapPlanner.Core.Tests;

public class GeocodingServiceTests
{
    private static PlaceDto Place(string name, double lat, double lng) => new()
    {
        Id = name,
        DisplayName = new PlaceDisplayNameDto { Text = name },
        Location = new PlaceLocationDto { Latitude = lat, Longitude = lng },
    };

    [Fact]
    public void PickBest_PrefersTheExactNameOverGooglesFirstResult()
    {
        // The reported bug: Google ranks the bigger store first.
        var places = new[]
        {
            Place("Nike Shibuya Scramble Square", 35.6585, 139.7023),
            Place("Nike Shibuya", 35.6640, 139.6990),
        };

        var best = GeocodingService.PickBest(places, "Nike Shibuya, Tokyo");

        Assert.Equal("Nike Shibuya", best?.DisplayName?.Text);
    }

    [Fact]
    public void PickBest_KeepsGooglesOrderOnATie()
    {
        var places = new[]
        {
            Place("Ichiran Shibuya", 35.66, 139.70),
            Place("Ichiran Shinjuku", 35.69, 139.70),
        };

        Assert.Equal("Ichiran Shibuya", GeocodingService.PickBest(places, "Ichiran, Tokyo")?.DisplayName?.Text);
    }

    [Fact]
    public void PickBest_NoNameOverlap_FallsBackToGooglesFirst()
    {
        // e.g. displayName came back in Japanese — nothing to compare, trust Google.
        var places = new[] { Place("渋谷スカイ", 35.658, 139.702), Place("渋谷", 35.66, 139.70) };

        Assert.Equal("渋谷スカイ", GeocodingService.PickBest(places, "Shibuya Sky, Tokyo")?.DisplayName?.Text);
    }

    [Fact]
    public void PickBest_SkipsCandidatesWithoutALocation()
    {
        var noLocation = new PlaceDto { Id = "x", DisplayName = new PlaceDisplayNameDto { Text = "Nike Shibuya" } };
        var places = new[] { noLocation, Place("Nike Shibuya Scramble Square", 35.6585, 139.7023) };

        Assert.Equal("Nike Shibuya Scramble Square", GeocodingService.PickBest(places, "Nike Shibuya")?.DisplayName?.Text);
    }

    [Fact]
    public void PickBest_EmptyList_IsNull() =>
        Assert.Null(GeocodingService.PickBest([], "Nike Shibuya"));

    [Fact]
    public void NameTokens_LowercasesAndSplitsOnPunctuation() =>
        Assert.Equal(new HashSet<string> { "senso", "ji", "temple" }, GeocodingService.NameTokens("Senso-ji Temple!"));
}
