using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using GmapPlanner.Core.Errors;
using GmapPlanner.Core.Services.Gemini;
using Xunit;

namespace GmapPlanner.Core.Tests;

/// <summary>
/// Ports gmap_planner/test_gemini_extract.py: covers the retry-once-on-bad-body /
/// no-retry-on-request-failure split, using a fake HttpMessageHandler in place of
/// FakeClient/ExplodingClient (input is a .txt file, so no Files API upload is hit).
/// </summary>
public class GeminiExtractionServiceTests
{
    private const string Good = """{"trip_name": "טיול", "days": [{"day": 1, "date": "01/06", "locations": []}]}""";

    [Fact]
    public async Task GoodResponse_IsReturnedWithoutRetry()
    {
        var handler = new FakeHandler(Envelope(Good));
        var trip = await Service(handler).ExtractItineraryAsync(await TempTxt());

        Assert.Equal("טיול", trip.TripName);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task TruncatedJson_IsRetried()
    {
        var handler = new FakeHandler(
            Envelope("""{"trip_name": "טיול", "days": [{"day": 1, "loc"""), // truncated mid-object
            Envelope(Good));
        var trip = await Service(handler).ExtractItineraryAsync(await TempTxt());

        Assert.Equal(1, trip.Days[0].DayNumber);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task EmptyResponse_IsRetried()
    {
        var handler = new FakeHandler(Envelope(null), Envelope(Good));
        var trip = await Service(handler).ExtractItineraryAsync(await TempTxt());

        Assert.Equal("טיול", trip.TripName);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task MissingRequiredFields_IsRetried()
    {
        var handler = new FakeHandler(Envelope("""{"days": []}"""), Envelope(Good));
        var trip = await Service(handler).ExtractItineraryAsync(await TempTxt());

        Assert.Equal("טיול", trip.TripName);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task FailedRequest_IsNotRetried()
    {
        // Retrying a quota/auth failure only doubles the wait and re-uploads the file.
        var handler = new FakeHandler(throwOnSend: true);

        var ex = await Assert.ThrowsAsync<PipelineException>(
            () => Service(handler).ExtractItineraryAsync(TempTxt().Result));

        Assert.Contains("request failed", ex.Message);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task TwoBadResponses_RaiseACleanError()
    {
        var handler = new FakeHandler(Envelope("not json"), Envelope("still not json"));

        var ex = await Assert.ThrowsAsync<BadResponseException>(
            () => Service(handler).ExtractItineraryAsync(TempTxt().Result));

        Assert.Contains("invalid/empty JSON", ex.Message);
        Assert.DoesNotContain("not json", ex.Message); // raw model output must never leak
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task SuggestTripName_ReturnsGeminisName()
    {
        var handler = new FakeHandler(Envelope("""{"trip_name": "טיול ליפן"}"""));
        var name = await Service(handler).SuggestTripNameAsync(new JsonObject { ["text"] = "x" }, "doc.pdf");
        Assert.Equal("טיול ליפן", name);
    }

    [Fact]
    public async Task SuggestTripName_BadResponse_FallsBackToFileStem()
    {
        var handler = new FakeHandler(Envelope("not json"));
        var name = await Service(handler).SuggestTripNameAsync(new JsonObject { ["text"] = "x" }, "Japan 2025.pdf");
        Assert.Equal("Japan 2025", name);
    }

    private static GeminiExtractionService Service(HttpMessageHandler handler) =>
        new(new HttpClient(handler), apiKey: "fake-key");

    private static async Task<string> TempTxt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gmap-planner-test-{Guid.NewGuid()}.txt");
        await File.WriteAllTextAsync(path, "Day 1: nothing much.");
        return path;
    }

    private static string Envelope(string? text)
    {
        if (text is null) return new JsonObject { ["candidates"] = new JsonArray() }.ToJsonString();
        return new JsonObject
        {
            ["candidates"] = new JsonArray(new JsonObject
            {
                ["content"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) },
                ["finishReason"] = "STOP",
            }),
        }.ToJsonString();
    }

    private sealed class FakeHandler(params string[] responseBodies) : HttpMessageHandler
    {
        private readonly Queue<string> _bodies = new(responseBodies);
        private readonly bool _throwOnSend;
        public int Calls { get; private set; }

        public FakeHandler(bool throwOnSend) : this(Array.Empty<string>()) => _throwOnSend = throwOnSend;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            if (_throwOnSend) throw new HttpRequestException("429 RESOURCE_EXHAUSTED");

            var body = _bodies.Dequeue();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
