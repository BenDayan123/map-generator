using System.Net.Http.Json;
using System.Text.Json.Serialization;
using GmapPlanner.Core.Errors;
using GmapPlanner.Core.Models;

namespace GmapPlanner.Core.Services;

/// <summary>Ports gmap_planner/geocode.py: snaps place names to exact coordinates.</summary>
public class GeocodingService(HttpClient http, string apiKey)
{
    // Geocoding API statuses that fail for EVERY request (bad/missing key, quota,
    // billing) — no point hammering the rest of the itinerary once seen.
    private static readonly HashSet<string> FatalStatuses = ["REQUEST_DENIED", "OVER_QUERY_LIMIT", "OVER_DAILY_LIMIT"];

    /// <summary>
    /// Resolves a place name to coordinates. Returns null on any recoverable failure.
    /// Throws PipelineException for a fatal, itinerary-wide failure (bad key, quota).
    /// </summary>
    public async Task<(double Lat, double Lng)?> GeocodePlaceAsync(string name, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(name)) return null;

        var url = $"{AppConfig.GeocodeUrl}?address={Uri.EscapeDataString(name)}&key={Uri.EscapeDataString(apiKey)}";

        GeocodeResponseDto? payload;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(15));
            payload = await http.GetFromJsonAsync<GeocodeResponseDto>(url, cts.Token);
        }
        catch (Exception e)
        {
            Console.WriteLine($"  ! Geocoding API request failed for '{name}': {e.Message}");
            return null;
        }

        var status = payload?.Status;
        if (status != "OK")
        {
            var message = payload?.ErrorMessage ?? "";
            if (status is not null && FatalStatuses.Contains(status))
            {
                var detail = string.IsNullOrEmpty(message) ? "" : $": {message}";
                throw new PipelineException($"Geocoding API error [{status}]{detail}");
            }
            if (status != "ZERO_RESULTS" && status is not null)
            {
                Console.WriteLine($"  ! Geocoding API error for '{name}': {status} {message}".TrimEnd());
            }
            return null;
        }

        var location = payload?.Results?.FirstOrDefault()?.Geometry?.Location;
        if (location?.Lat is null || location.Lng is null) return null;
        return (location.Lat.Value, location.Lng.Value);
    }

    /// <summary>
    /// Snaps every location's coords to its exact Google Maps point in place, falling
    /// back to Gemini's coords for any location that can't be resolved.
    /// Returns (correctedCount, fallbackCount).
    /// </summary>
    public async Task<(int Corrected, int Fallback)> GeocodeItineraryAsync(Trip trip, CancellationToken ct = default)
    {
        var locations = trip.Days.SelectMany(d => d.Locations).ToList();

        if (string.IsNullOrEmpty(apiKey))
        {
            Console.WriteLine("  ! Geocoding API skipped: no key provided. Keeping Gemini's coordinates.");
            return (0, locations.Count);
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
                return (corrected, fallback + remaining);
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
        return (corrected, fallback);
    }
}

internal record GeocodeResponseDto
{
    [JsonPropertyName("status")] public string? Status { get; init; }
    [JsonPropertyName("error_message")] public string? ErrorMessage { get; init; }
    [JsonPropertyName("results")] public List<GeocodeResultDto>? Results { get; init; }
}

internal record GeocodeResultDto
{
    [JsonPropertyName("geometry")] public GeocodeGeometryDto? Geometry { get; init; }
}

internal record GeocodeGeometryDto
{
    [JsonPropertyName("location")] public GeocodeLocationDto? Location { get; init; }
}

internal record GeocodeLocationDto
{
    [JsonPropertyName("lat")] public double? Lat { get; init; }
    [JsonPropertyName("lng")] public double? Lng { get; init; }
}
