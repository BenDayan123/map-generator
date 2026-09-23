using System.Net;
using System.Text;
using System.Text.Json.Nodes;
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
    public void PickBest_PartialOverlapNeverBeatsGooglesFirst()
    {
        // Accented English name from Google vs Gemini's plain spelling + descriptor word.
        var places = new[]
        {
            Place("Tōdai-ji", 34.6890, 135.8398),
            Place("Todai-ji Temple Museum", 34.6870, 135.8380),
        };

        Assert.Equal("Tōdai-ji", GeocodingService.PickBest(places, "Todai-ji Temple, Nara")?.DisplayName?.Text);
    }

    [Fact]
    public void NameTokens_LowercasesAndSplitsOnPunctuation() =>
        Assert.Equal(new HashSet<string> { "senso", "ji", "temple" }, GeocodingService.NameTokens("Senso-ji Temple!"));

    [Fact]
    public async Task GeocodePlace_RequestsSeveralEnglishCandidatesNearGeminiAndPicksTheNameMatch()
    {
        const string response = """
            {"places":[
              {"id":"a","displayName":{"text":"Nike Shibuya Scramble Square"},"location":{"latitude":35.6585,"longitude":139.7023}},
              {"id":"b","displayName":{"text":"Nike Shibuya"},"location":{"latitude":35.6640,"longitude":139.6990}}
            ]}
            """;
        var handler = new CapturingHandler(response);
        var service = new GeocodingService(new HttpClient(handler), "fake-key");

        var result = await service.GeocodePlaceAsync("Nike Shibuya, Tokyo", biasLat: 35.66, biasLng: 139.70);

        Assert.Equal((35.6640, 139.6990), result);
        var body = JsonNode.Parse(handler.Body!)!;
        Assert.Equal("Nike Shibuya, Tokyo", body["textQuery"]!.GetValue<string>());
        Assert.Equal(5, body["pageSize"]!.GetValue<int>());
        Assert.Equal("en", body["languageCode"]!.GetValue<string>());
        Assert.Equal(35.66, body["locationBias"]!["circle"]!["center"]!["latitude"]!.GetValue<double>());
        Assert.Equal(139.70, body["locationBias"]!["circle"]!["center"]!["longitude"]!.GetValue<double>());
        Assert.Equal(50_000, body["locationBias"]!["circle"]!["radius"]!.GetValue<double>());
    }

    [Fact]
    public async Task GeocodePlace_WithoutCoords_SendsNoLocationBias()
    {
        var handler = new CapturingHandler("""{"places":[]}""");
        var service = new GeocodingService(new HttpClient(handler), "fake-key");

        Assert.Null(await service.GeocodePlaceAsync("Nike Shibuya"));
        Assert.Null(JsonNode.Parse(handler.Body!)!["locationBias"]);
    }

    private sealed class CapturingHandler(string responseBody) : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = await request.Content!.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            };
        }
    }
}
