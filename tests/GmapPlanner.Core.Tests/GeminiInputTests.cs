using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using GmapPlanner.Core.Errors;
using GmapPlanner.Core.Services.Gemini;
using Xunit;

namespace GmapPlanner.Core.Tests;

/// <summary>
/// The browser host has bytes, not paths, and can't read the Files API's upload-URL header
/// cross-origin — so it extracts from bytes and sends PDFs inline.
/// </summary>
public class GeminiInputTests
{
    private const string Good = """{"trip_name": "Kyoto", "days": [{"day": 1, "date": "", "locations": []}]}""";

    [Fact]
    public async Task TxtBytes_AreSentAsText_WithBomStripped()
    {
        var handler = new RecordingHandler(Envelope(Good));
        var content = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("Day 1: קיוטו")).ToArray();

        var trip = await new GeminiExtractionService(new HttpClient(handler), apiKey: "k")
            .ExtractItineraryAsync("trip.txt", content);

        Assert.Equal("Kyoto", trip.TripName);
        var parts = JsonNode.Parse(Assert.Single(handler.Bodies))!["contents"]![0]!["parts"]!.AsArray();
        Assert.Equal("Day 1: קיוטו", parts[1]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task InlineFiles_SendsPdfAsBase64InlineData_WithoutUpload()
    {
        var handler = new RecordingHandler(Envelope(Good));

        await new GeminiExtractionService(new HttpClient(handler), apiKey: "k", inlineFiles: true)
            .ExtractItineraryAsync("trip.pdf", [1, 2, 3]);

        var uri = Assert.Single(handler.Uris);
        Assert.Contains(":generateContent", uri);
        var inline = JsonNode.Parse(handler.Bodies[0])!["contents"]![0]!["parts"]![1]!["inline_data"]!;
        Assert.Equal("application/pdf", inline["mime_type"]!.GetValue<string>());
        Assert.Equal("AQID", inline["data"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnsupportedExtension_Throws()
    {
        var handler = new RecordingHandler(Envelope(Good));

        var ex = await Assert.ThrowsAsync<PipelineException>(() =>
            new GeminiExtractionService(new HttpClient(handler), apiKey: "k", inlineFiles: true)
                .ExtractItineraryAsync("trip.docx", [1]));

        Assert.Contains("trip.docx", ex.Message);
        Assert.Empty(handler.Uris);
    }

    private static string Envelope(string text) => new JsonObject
    {
        ["candidates"] = new JsonArray(new JsonObject
        {
            ["content"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) },
            ["finishReason"] = "STOP",
        }),
    }.ToJsonString();

    private sealed class RecordingHandler(string body) : HttpMessageHandler
    {
        public List<string> Uris { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uris.Add(request.RequestUri!.ToString());
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
