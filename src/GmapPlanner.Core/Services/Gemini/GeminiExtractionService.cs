using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GmapPlanner.Core.Errors;
using GmapPlanner.Core.Json;
using GmapPlanner.Core.Models;

namespace GmapPlanner.Core.Services.Gemini;

/// <summary>
/// Ports gmap_planner/gemini.py: loads a .txt/.pdf itinerary and extracts it via the
/// Gemini generateContent REST API (no SDK — HttpClient direct against
/// generativelanguage.googleapis.com), with structured JSON output and one retry on
/// an unusable response body.
/// </summary>
public class GeminiExtractionService(HttpClient http, string apiKey, string? promptOverride = null)
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com";

    // Defaults to the shipped extraction prompt; overridable for A/B testing a prompt change.
    private string PromptText => promptOverride ?? Prompt.ExtractionPrompt.Text;

    // A bad response is usually a one-off (truncation, a blocked/empty candidate), and
    // sampling is stochastic, so one plain retry fixes most of them. A failed *request*
    // (bad key, quota, network) is not retried here — see ExtractItineraryAsync.
    private const int Attempts = 2;

    // Mirrors prompt.py / gemini.py's response_schema so Gemini's JSON output matches
    // what we deserialize below.
    private const string ResponseSchemaJson = """
        {
          "type": "OBJECT",
          "required": ["trip_name", "days"],
          "properties": {
            "trip_name": { "type": "STRING" },
            "days": {
              "type": "ARRAY",
              "items": {
                "type": "OBJECT",
                "required": ["day", "date", "locations"],
                "properties": {
                  "day": { "type": "INTEGER" },
                  "date": { "type": "STRING" },
                  "locations": {
                    "type": "ARRAY",
                    "items": {
                      "type": "OBJECT",
                      "required": ["name", "lat", "lng", "notes"],
                      "properties": {
                        "name": { "type": "STRING" },
                        "lat": { "type": "NUMBER" },
                        "lng": { "type": "NUMBER" },
                        "notes": { "type": "STRING" }
                      }
                    }
                  }
                }
              }
            }
          }
        }
        """;

    public async Task<Trip> ExtractItineraryAsync(string filePath, CancellationToken ct = default) =>
        await ExtractItineraryAsync(await BuildDocumentPartAsync(filePath, ct), ct);

    /// <summary>Extracts from an already-built document part (see <see cref="BuildDocumentPartAsync"/>).</summary>
    public async Task<Trip> ExtractItineraryAsync(JsonObject documentPart, CancellationToken ct = default)
    {
        List<JsonObject> parts = [new JsonObject { ["text"] = PromptText }, documentPart];

        PipelineException last = new("Gemini API (location extraction) was never called.");
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            try
            {
                return await ExtractOnceAsync(parts, ct);
            }
            catch (BadResponseException e)
            {
                last = e;
            }
        }
        throw last;
    }

    /// <summary>
    /// Asks Gemini for a short Hebrew trip name, from the file name when it is meaningful,
    /// else from the document body. Best-effort: any failure returns the file-name stem.
    /// </summary>
    public async Task<string> SuggestTripNameAsync(JsonObject documentPart, string fileName, CancellationToken ct = default)
    {
        var fallback = Path.GetFileNameWithoutExtension(fileName);
        var body = new JsonObject
        {
            ["contents"] = new JsonArray(new JsonObject
            {
                ["parts"] = new JsonArray(
                    new JsonObject { ["text"] = Prompt.TripNamePrompt.For(fileName) },
                    documentPart.DeepClone()),
            }),
            ["generationConfig"] = new JsonObject
            {
                ["temperature"] = 0,
                ["responseMimeType"] = "application/json",
                ["responseSchema"] = JsonNode.Parse("""
                    { "type": "OBJECT", "required": ["trip_name"], "properties": { "trip_name": { "type": "STRING" } } }
                    """),
            },
        };
        try
        {
            using var response = await http.PostAsync(
                $"{BaseUrl}/v1beta/models/{AppConfig.GeminiModel}:generateContent?key={apiKey}",
                new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
                ct);
            if (!response.IsSuccessStatusCode) return fallback;
            var envelope = await response.Content.ReadFromJsonAsync(
                GmapPlannerJsonContext.Default.GenerateContentResponse, ct);
            var text = envelope?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;
            var name = text is null ? null : JsonNode.Parse(text)?["trip_name"]?.GetValue<string>()?.Trim();
            return string.IsNullOrEmpty(name) ? fallback : name;
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return fallback;
        }
    }

    /// <summary>
    /// The itinerary as a Gemini content part: inline text for .txt, otherwise a Files API
    /// upload. Built once per run so extraction and the name suggestion share one upload.
    /// </summary>
    public async Task<JsonObject> BuildDocumentPartAsync(string filePath, CancellationToken ct = default)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();

        if (ext == ".txt")
        {
            string text;
            try
            {
                text = await File.ReadAllTextAsync(filePath, Encoding.UTF8, ct);
            }
            catch (Exception e)
            {
                throw new PipelineException($"Failed to read '{filePath}': {e.Message}", e);
            }
            return new JsonObject { ["text"] = text };
        }

        var (uri, mimeType) = await UploadFileAsync(filePath, ct);
        return new JsonObject
        {
            ["file_data"] = new JsonObject { ["mime_type"] = mimeType, ["file_uri"] = uri },
        };
    }

    private async Task<Trip> ExtractOnceAsync(List<JsonObject> parts, CancellationToken ct)
    {
        // NB: no max output token limit is set — unset means the model's own maximum,
        // and any value picked here could only lower the ceiling and cause the
        // mid-JSON truncation this retry exists to survive.
        var body = new JsonObject
        {
            ["contents"] = new JsonArray(
                new JsonObject { ["parts"] = new JsonArray(parts.Select(p => (JsonNode)p.DeepClone()).ToArray()) }
            ),
            ["generationConfig"] = new JsonObject
            {
                // Extraction is deterministic transcription, not creative writing. Default sampling
                // (~1.0) occasionally reshuffles a day boundary — the intermittent "places drifted
                // to the next day" bug. temperature 0 makes the same itinerary extract the same way.
                ["temperature"] = 0,
                ["responseMimeType"] = "application/json",
                ["responseSchema"] = JsonNode.Parse(ResponseSchemaJson),
            },
        };

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync(
                $"{BaseUrl}/v1beta/models/{AppConfig.GeminiModel}:generateContent?key={apiKey}",
                new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
                ct);
        }
        catch (Exception e)
        {
            throw new PipelineException($"Gemini API (location extraction) request failed: {e.Message}", e);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new PipelineException(
                $"Gemini API (location extraction) request failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
        }

        var envelope = await response.Content.ReadFromJsonAsync(
            GmapPlannerJsonContext.Default.GenerateContentResponse, ct);
        var candidate = envelope?.Candidates?.FirstOrDefault();
        var text = candidate?.Content?.Parts?.FirstOrDefault()?.Text;
        var finishReason = candidate?.FinishReason ?? "unknown";

        ExtractionResultDto? result = null;
        if (!string.IsNullOrEmpty(text))
        {
            try
            {
                result = JsonSerializer.Deserialize(text, GmapPlannerJsonContext.Default.ExtractionResultDto);
            }
            catch (JsonException)
            {
                // response_schema makes this rare; when it still happens it's usually a
                // truncated (MAX_TOKENS) or blocked (SAFETY) response, not real prose.
            }
        }

        if (result?.TripName is null || result.Days is null)
        {
            throw new BadResponseException(
                $"Gemini API (location extraction) returned invalid/empty JSON (finish_reason: {finishReason}).");
        }

        return new Trip
        {
            TripName = result.TripName,
            Days = result.Days.Select(d => new Day
            {
                DayNumber = d.Day,
                Date = d.Date ?? "",
                Locations = (d.Locations ?? []).Select(l => new Location
                {
                    Name = l.Name ?? "",
                    Lat = l.Lat,
                    Lng = l.Lng,
                    Notes = l.Notes ?? "",
                }).ToList(),
            }).ToList(),
        };
    }

    private async Task<(string Uri, string MimeType)> UploadFileAsync(string filePath, CancellationToken ct)
    {
        var mimeType = Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            _ => throw new PipelineException($"Unsupported file type for Gemini upload: '{filePath}' (expected .txt or .pdf)."),
        };
        var displayName = Path.GetFileName(filePath);
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(filePath, ct);
        }
        catch (Exception e)
        {
            throw new PipelineException($"Gemini Files API failed to upload '{displayName}': {e.Message}", e);
        }

        var metadata = new JsonObject
        {
            ["file"] = new JsonObject { ["display_name"] = displayName },
        }.ToJsonString();
        using var startRequest = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/upload/v1beta/files?key={apiKey}")
        {
            Content = new StringContent(metadata, Encoding.UTF8, "application/json"),
        };
        startRequest.Headers.Add("X-Goog-Upload-Protocol", "resumable");
        startRequest.Headers.Add("X-Goog-Upload-Command", "start");
        startRequest.Headers.Add("X-Goog-Upload-Header-Content-Length", bytes.Length.ToString());
        startRequest.Headers.Add("X-Goog-Upload-Header-Content-Type", mimeType);

        HttpResponseMessage startResponse;
        try
        {
            startResponse = await http.SendAsync(startRequest, ct);
        }
        catch (Exception e)
        {
            throw new PipelineException($"Gemini Files API failed to upload '{displayName}': {e.Message}", e);
        }
        if (!startResponse.IsSuccessStatusCode)
        {
            throw new PipelineException(
                $"Gemini Files API failed to upload '{displayName}': HTTP {(int)startResponse.StatusCode} {startResponse.ReasonPhrase}");
        }
        if (!startResponse.Headers.TryGetValues("X-Goog-Upload-URL", out var uploadUrls))
        {
            throw new PipelineException($"Gemini Files API did not return an upload URL for '{displayName}'.");
        }

        using var uploadRequest = new HttpRequestMessage(HttpMethod.Put, uploadUrls.First())
        {
            Content = new ByteArrayContent(bytes),
        };
        uploadRequest.Headers.Add("X-Goog-Upload-Offset", "0");
        uploadRequest.Headers.Add("X-Goog-Upload-Command", "upload, finalize");

        HttpResponseMessage uploadResponse;
        try
        {
            uploadResponse = await http.SendAsync(uploadRequest, ct);
        }
        catch (Exception e)
        {
            throw new PipelineException($"Gemini Files API failed to upload '{displayName}': {e.Message}", e);
        }
        if (!uploadResponse.IsSuccessStatusCode)
        {
            throw new PipelineException(
                $"Gemini Files API failed to upload '{displayName}': HTTP {(int)uploadResponse.StatusCode} {uploadResponse.ReasonPhrase}");
        }

        var envelope = await uploadResponse.Content.ReadFromJsonAsync(
            GmapPlannerJsonContext.Default.UploadFileEnvelope, ct);
        var file = envelope?.File
            ?? throw new PipelineException($"Gemini Files API returned no file info for '{displayName}'.");

        // PDFs are typically ACTIVE immediately; poll briefly in case processing lags.
        for (var i = 0; i < 10 && file.State == "PROCESSING"; i++)
        {
            await Task.Delay(1000, ct);
            file = await GetFileStatusAsync(file.Name!, ct);
        }
        if (file.State == "FAILED")
        {
            throw new PipelineException($"Gemini Files API failed to process '{displayName}'.");
        }

        return (file.Uri ?? throw new PipelineException($"Gemini Files API returned no URI for '{displayName}'."),
            file.MimeType ?? mimeType);
    }

    private async Task<UploadedFile> GetFileStatusAsync(string name, CancellationToken ct)
    {
        var response = await http.GetAsync($"{BaseUrl}/v1beta/{name}?key={apiKey}", ct);
        response.EnsureSuccessStatusCode();
        var file = await response.Content.ReadFromJsonAsync(GmapPlannerJsonContext.Default.UploadedFile, ct);
        return file ?? throw new PipelineException($"Gemini Files API returned no status for '{name}'.");
    }
}
