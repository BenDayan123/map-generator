using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using GmapPlanner.Core.Errors;
using GmapPlanner.Core.Json;
using GmapPlanner.Core.Models;

namespace GmapPlanner.Core.Services;

/// <summary>
/// Snaps place names to exact coordinates via Places API (New) Text Search — a place-name
/// search, unlike the Geocoding API, which is an address geocoder and mis-resolves POIs.
/// </summary>
public class GeocodingService(HttpClient http, string apiKey)
{
    // Error statuses that fail for EVERY request (API not enabled/billing/key restrictions,
    // quota, bad key) — no point hammering the rest of the itinerary once seen.
    private static readonly HashSet<string> FatalStatuses = ["PERMISSION_DENIED", "RESOURCE_EXHAUSTED", "UNAUTHENTICATED"];

    // Candidates to compare. Text Search bills per request, not per result, so 5 is free.
    private const int CandidateCount = 5;
    // Bias radius around Gemini's own guess — keeps a same-name place in another city out of
    // the top results. 50 km is the API maximum; it biases, it doesn't restrict.
    private const double BiasRadiusMeters = 50_000;

    /// <summary>
    /// Resolves a place name to coordinates. Returns null on any recoverable failure.
    /// Throws PipelineException for a fatal, itinerary-wide failure (bad key, quota).
    /// </summary>
    public async Task<(double Lat, double Lng)?> GeocodePlaceAsync(
        string name, double? biasLat = null, double? biasLng = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(name)) return null;

        // languageCode=en: display names come back in English, so PickBest can compare them
        // with Gemini's (mostly English) names instead of e.g. Japanese.
        var body = new JsonObject
        {
            ["textQuery"] = name,
            ["pageSize"] = CandidateCount,
            ["languageCode"] = "en",
        };
        if (biasLat is { } lat && biasLng is { } lng && (lat != 0 || lng != 0))
        {
            body["locationBias"] = new JsonObject
            {
                ["circle"] = new JsonObject
                {
                    ["center"] = new JsonObject { ["latitude"] = lat, ["longitude"] = lng },
                    ["radius"] = BiasRadiusMeters,
                },
            };
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, AppConfig.PlacesTextSearchUrl)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Goog-Api-Key", apiKey);
        request.Headers.Add("X-Goog-FieldMask", AppConfig.PlacesFieldMask);

        PlacesSearchResponseDto? payload;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await http.SendAsync(request, cts.Token);
            payload = await response.Content.ReadFromJsonAsync(GmapPlannerJsonContext.Default.PlacesSearchResponseDto, cts.Token);
        }
        catch (Exception e)
        {
            Console.WriteLine($"  ! Places API request failed for '{name}': {e.Message}");
            return null;
        }

        if (payload?.Error is { } error)
        {
            var status = error.Status ?? "";
            var message = error.Message ?? "";
            // A bad key comes back as INVALID_ARGUMENT, which is otherwise per-request.
            if (FatalStatuses.Contains(status) || message.Contains("API key", StringComparison.OrdinalIgnoreCase))
            {
                var detail = string.IsNullOrEmpty(message) ? "" : $": {message}";
                throw new PipelineException($"Places API error [{status}]{detail}");
            }
            Console.WriteLine($"  ! Places API error for '{name}': {status} {message}".TrimEnd());
            return null;
        }

        // No match is an empty body ({}), not an error status.
        var best = PickBest(payload?.Places ?? [], name);
        if (best?.Location is not { Latitude: { } bestLat, Longitude: { } bestLng }) return null;
        return (bestLat, bestLng);
    }

    /// <summary>
    /// Snaps every location's coords to its exact Google Maps point in place, falling
    /// back to Gemini's coords for any location that can't be resolved.
    /// Returns (correctedCount, fallbackCount, warning). Warning is a user-facing reason
    /// when geocoding produced no exact coordinates — a missing key or a fatal API error
    /// such as REQUEST_DENIED (billing not enabled). It's otherwise null. Without this the
    /// reason only reached the console, leaving a GUI user staring at "0/N" with no cause.
    /// </summary>
    public async Task<(int Corrected, int Fallback, string? Warning)> GeocodeItineraryAsync(
        Trip trip, CancellationToken ct = default)
    {
        var locations = trip.Days.SelectMany(d => d.Locations).ToList();

        if (string.IsNullOrEmpty(apiKey))
        {
            const string msg = "Geocoding skipped: no Places API key set. Keeping Gemini's coordinates.";
            Console.WriteLine($"  ! {msg}");
            return (0, locations.Count, msg);
        }

        var corrected = 0;
        var fallback = 0;
        foreach (var loc in locations)
        {
            (double Lat, double Lng)? result;
            try
            {
                result = await GeocodePlaceAsync(loc.Name, loc.Lat, loc.Lng, ct);
            }
            catch (PipelineException e)
            {
                var remaining = locations.Count - corrected - fallback;
                Console.WriteLine($"  ! {e.Message}\n    Keeping Gemini's coordinates for {remaining} remaining location(s).");
                return (corrected, fallback + remaining, e.Message);
            }
            if (result is not null)
            {
                loc.Lat = result.Value.Lat;
                loc.Lng = result.Value.Lng;
                corrected++;
            }
            else
            {
                fallback++;
            }
        }
        return (corrected, fallback, null);
    }

    /// <summary>
    /// Picks the candidate whose display name best matches the extracted name. Google ranks
    /// by relevance + prominence, so a popular place whose name merely *contains* the query
    /// ("Nike Shibuya Scramble Square") can outrank the exact match ("Nike Shibuya").
    /// Score = token Jaccard overlap against the name before its ", City" suffix. Only a
    /// strictly higher score beats an earlier candidate, so ties keep Google's order.
    /// </summary>
    internal static PlaceDto? PickBest(IReadOnlyList<PlaceDto> places, string name)
    {
        var wanted = NameTokens(name.Split(',')[0]);
        PlaceDto? best = null;
        var bestScore = -1.0;
        foreach (var place in places)
        {
            if (place.Location?.Latitude is null || place.Location.Longitude is null) continue;
            var got = NameTokens(place.DisplayName?.Text ?? "");
            var union = wanted.Union(got).Count();
            var score = union == 0 ? 0 : (double)wanted.Intersect(got).Count() / union;
            if (score > bestScore)
            {
                best = place;
                bestScore = score;
            }
        }
        return best;
    }

    /// <summary>
    /// Lowercase letter/digit tokens. Deliberately no Unicode normalization —
    /// InvariantGlobalization is on (see CLAUDE.md), so stick to char-level APIs.
    /// </summary>
    internal static HashSet<string> NameTokens(string text)
    {
        var chars = text.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ').ToArray();
        return new string(chars).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
    }
}

internal record PlacesSearchResponseDto
{
    [JsonPropertyName("places")] public List<PlaceDto>? Places { get; init; }
    [JsonPropertyName("error")] public PlacesErrorDto? Error { get; init; }
}

internal record PlaceDto
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("displayName")] public PlaceDisplayNameDto? DisplayName { get; init; }
    [JsonPropertyName("location")] public PlaceLocationDto? Location { get; init; }
}

internal record PlaceDisplayNameDto
{
    [JsonPropertyName("text")] public string? Text { get; init; }
}

internal record PlaceLocationDto
{
    [JsonPropertyName("latitude")] public double? Latitude { get; init; }
    [JsonPropertyName("longitude")] public double? Longitude { get; init; }
}

internal record PlacesErrorDto
{
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("message")] public string? Message { get; init; }
}
