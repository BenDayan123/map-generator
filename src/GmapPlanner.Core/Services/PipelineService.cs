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
    public List<KmlFile> KmlFiles { get; init; } = [];
    public List<string> Files { get; init; } = [];
    public string OutputDir { get; init; } = "";
}

public delegate void ProgressCallback(string step, double fraction);

/// <summary>Ports gmap_planner/service.py's run_pipeline: extract -> geocode -> build KML.</summary>
public class PipelineService(GeminiExtractionService gemini, GeocodingService geocoding)
{
    /// <summary>
    /// Extract -> geocode -> KML, held in memory (the browser host offers the files as
    /// downloads). Nothing is written to disk; Files and OutputDir stay empty.
    /// </summary>
    public async Task<PipelineResult> GenerateAsync(
        string filePath,
        int layersPerFile = AppConfig.MaxLayersPerFile,
        bool noGeocode = false,
        ProgressCallback? progress = null,
        CancellationToken ct = default)
    {
        progress?.Invoke("Extracting locations with Gemini", 0.35);
        var trip = await gemini.ExtractItineraryAsync(filePath, ct);
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

        progress?.Invoke("Building KML files", 0.85);
        var kmlFiles = KmlBuilder.BuildKmlFiles(KmlBuilder.ChunkDays(trip.Days, layersPerFile));

        return new PipelineResult
        {
            TripName = trip.TripName,
            Days = trip.Days.Count,
            Locations = trip.Days.Sum(d => d.Locations.Count),
            Corrected = corrected,
            Fallback = fallback,
            GeocodeWarning = geocodeWarning,
            KmlFiles = kmlFiles,
        };
    }

    /// <summary>Desktop path: <see cref="GenerateAsync"/>, then write the KML files under outputDir/{trip}.</summary>
    public async Task<PipelineResult> RunAsync(
        string filePath,
        string outputDir,
        int layersPerFile = AppConfig.MaxLayersPerFile,
        bool noGeocode = false,
        ProgressCallback? progress = null,
        CancellationToken ct = default)
    {
        var result = await GenerateAsync(filePath, layersPerFile, noGeocode, progress, ct);

        var tripDir = Path.Combine(outputDir, KmlBuilder.SanitizeFolderName(result.TripName));
        var files = KmlBuilder.SaveKmlFiles(result.KmlFiles, tripDir);

        progress?.Invoke("Done", 1.0);
        return result with { Files = files, OutputDir = tripDir };
    }
}
