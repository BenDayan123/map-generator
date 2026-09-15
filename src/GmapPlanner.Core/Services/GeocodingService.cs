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

    /// <summary>
    /// Resolves a place name to coordinates. Returns null on any recoverable failure.
    /// Throws PipelineException for a fatal, itinerary-wide failure (bad key, quota).
    /// </summary>
    public async Task<(double Lat, double Lng)?> GeocodePlaceAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(name)) return null;

        using var request = new HttpRequestMessage(HttpMethod.Post, AppConfig.PlacesTextSearchUrl)
        {
            Content = new StringContent(new JsonObject { ["textQuery"] = name }.ToJsonString(), Encoding.UTF8, "application/json"),
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
        var location = payload?.Places?.FirstOrDefault()?.Location;
        if (location?.Latitude is null || location.Longitude is null) return null;
        return (location.Latitude.Value, location.Longitude.Value);
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
                result = await GeocodePlaceAsync(loc.Name, ct);
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
