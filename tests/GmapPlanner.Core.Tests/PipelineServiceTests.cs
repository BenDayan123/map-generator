using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using GmapPlanner.Core.Services;
using GmapPlanner.Core.Services.Gemini;
using Xunit;

namespace GmapPlanner.Core.Tests;

/// <summary>
/// GenerateAsync is the browser path: extract -> geocode -> KML, all in memory. RunAsync is
/// the desktop path layered on top. Geocoding runs with an empty key, which skips the Places
/// API entirely, so the only HTTP call is the faked Gemini one.
/// </summary>
public class PipelineServiceTests
{
    private const string TripJson = """
        {"trip_name": "Tokyo/Kyoto Trip", "days": [
          {"day": 1, "date": "01/06", "locations": [{"name": "Nishiki Market, Kyoto", "lat": 35.005, "lng": 135.765, "notes": ""}]},
          {"day": 2, "date": "02/06", "locations": [{"name": "Senso-ji, Tokyo", "lat": 35.714, "lng": 139.796, "notes": ""}]}
        ]}
        """;

    [Fact]
    public async Task GenerateAsync_ReturnsKmlInMemoryWithoutWritingFiles()
    {
        var result = await Pipeline().GenerateAsync(await TempTxt(), layersPerFile: 10);

        Assert.Equal("Tokyo/Kyoto Trip", result.TripName);
        Assert.Equal(2, result.Days);
        Assert.Equal(2, result.Locations);
        Assert.Equal(2, result.Fallback); // no Places key -> Gemini's coords kept
        var kml = Assert.Single(result.KmlFiles);
        Assert.Equal("1-2.kml", kml.FileName);
        Assert.Contains("Senso-ji, Tokyo", kml.Content);
        Assert.Empty(result.Files);
        Assert.Equal("", result.OutputDir);
    }

    [Fact]
    public async Task RunAsync_SavesTheGeneratedKmlUnderTheTripFolder()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), "gmap-planner-tests-" + Guid.NewGuid());
        try
        {
            var result = await Pipeline().RunAsync(await TempTxt(), outputDir, layersPerFile: 1);

            Assert.Equal(Path.Combine(outputDir, "TokyoKyoto Trip"), result.OutputDir);
            Assert.Equal(new[] { "1.kml", "2.kml" }, result.KmlFiles.Select(f => f.FileName));
            Assert.Equal(result.KmlFiles.Select(f => Path.Combine(result.OutputDir, f.FileName)), result.Files);
            Assert.All(result.Files, p => Assert.True(File.Exists(p)));
        }
        finally
        {
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    private static PipelineService Pipeline()
    {
        var http = new HttpClient(new OneResponseHandler(Envelope(TripJson)));
        return new PipelineService(new GeminiExtractionService(http, apiKey: "fake-key"), new GeocodingService(http, apiKey: ""));
    }

    private static async Task<string> TempTxt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gmap-planner-test-{Guid.NewGuid()}.txt");
        await File.WriteAllTextAsync(path, "Day 1: Kyoto. Day 2: Tokyo.");
        return path;
    }

    private static string Envelope(string text) => new JsonObject
    {
        ["candidates"] = new JsonArray(new JsonObject
        {
            ["content"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) },
            ["finishReason"] = "STOP",
        }),
    }.ToJsonString();

    private sealed class OneResponseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
