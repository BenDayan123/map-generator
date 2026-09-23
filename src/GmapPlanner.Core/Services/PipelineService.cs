using GmapPlanner.Core.Errors;
using GmapPlanner.Core.Services.Gemini;

namespace GmapPlanner.Core.Services;

public record PipelineResult
{
    public required string TripName { get; init; }
    public int Days { get; init; }
    public int Locations { get; init; }
    public int Corrected { get; init; }
    public int Fallback { get; init; }
    public string? GeocodeWarning { get; init; }
    public List<string> Files { get; init; } = [];
    public string OutputDir { get; init; } = "";
}

public delegate void ProgressCallback(string step, double fraction);

/// <summary>Ports gmap_planner/service.py's run_pipeline: extract -> geocode -> write KML.</summary>
public class PipelineService(GeminiExtractionService gemini, GeocodingService geocoding)
{
    public async Task<PipelineResult> RunAsync(
        string filePath,
        string outputDir,
        int layersPerFile = AppConfig.MaxLayersPerFile,
        bool noGeocode = false,
        ProgressCallback? progress = null,
        Func<string, Task<string>>? confirmTripName = null,
        CancellationToken ct = default)
    {
        progress?.Invoke("Extracting locations with Gemini", 0.35);
        var document = await gemini.BuildDocumentPartAsync(filePath, ct);

        // The name prompt runs alongside extraction/geocoding; only writing the KML files
        // (the folder is named after the trip) and everything after it wait for the user.
        var nameTask = confirmTripName is null
            ? null
            : ConfirmNameAsync(document, Path.GetFileName(filePath), confirmTripName, ct);

        var trip = await gemini.ExtractItineraryAsync(document, ct);
        if (trip.Days.Count == 0)
            throw new PipelineException("No days found in the extracted itinerary.");

        var corrected = 0;
        var fallback = 0;
        string? geocodeWarning = null;
        if (!noGeocode)
        {
            progress?.Invoke("Snapping place names to exact coordinates", 0.6);
            (corrected, fallback, geocodeWarning) = await geocoding.GeocodeItineraryAsync(trip, ct);
        }

        if (nameTask is not null)
        {
            progress?.Invoke("Waiting for the trip name to be approved", 0.8);
            trip.TripName = await nameTask;
        }

        progress?.Invoke("Writing KML files", 0.85);
        var chunks = KmlBuilder.ChunkDays(trip.Days, layersPerFile);
        var tripFolder = KmlBuilder.SanitizeFolderName(trip.TripName);
        var tripDir = Path.Combine(outputDir, tripFolder);
        var files = KmlBuilder.WriteKmlFiles(chunks, tripDir);

        progress?.Invoke("Done", 1.0);
        return new PipelineResult
        {
            TripName = trip.TripName,
            Days = trip.Days.Count,
            Locations = trip.Days.Sum(d => d.Locations.Count),
            Corrected = corrected,
            Fallback = fallback,
            GeocodeWarning = geocodeWarning,
            Files = files,
            OutputDir = tripDir,
        };
    }

    private async Task<string> ConfirmNameAsync(
        System.Text.Json.Nodes.JsonObject document, string fileName,
        Func<string, Task<string>> confirm, CancellationToken ct)
    {
        var suggested = await gemini.SuggestTripNameAsync(document, fileName, ct);
        return await confirm(suggested);
    }
}
