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
        Func<string, Task<string>>? confirmTripName = null,
        CancellationToken ct = default)
    {
        byte[] content;
        try
        {
            content = await File.ReadAllBytesAsync(filePath, ct);
        }
        catch (Exception e)
        {
            throw new PipelineException($"Failed to read '{filePath}': {e.Message}", e);
        }
        return await GenerateAsync(Path.GetFileName(filePath), content, layersPerFile, noGeocode, progress, confirmTripName, ct);
    }

    /// <summary>Same as the path overload, for an itinerary already in memory (the browser host).</summary>
    /// <remarks>
    /// With <paramref name="confirmTripName"/>, Gemini's suggested Hebrew name is handed to it while
    /// extraction and geocoding keep running; building the KML (and everything after) waits for
    /// the approved name. Cancelling <paramref name="ct"/> aborts the run.
    /// </remarks>
    public async Task<PipelineResult> GenerateAsync(
        string fileName,
        byte[] content,
        int layersPerFile = AppConfig.MaxLayersPerFile,
        bool noGeocode = false,
        ProgressCallback? progress = null,
        Func<string, Task<string>>? confirmTripName = null,
        CancellationToken ct = default)
    {
        progress?.Invoke("Extracting locations with Gemini", 0.35);
        var document = await gemini.BuildDocumentPartAsync(fileName, content, ct);

        var nameTask = confirmTripName is null
            ? null
            : ConfirmNameAsync(document, fileName, confirmTripName, ct);

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
        Func<string, Task<string>>? confirmTripName = null,
        CancellationToken ct = default)
    {
        var result = await GenerateAsync(filePath, layersPerFile, noGeocode, progress, confirmTripName, ct);

        var tripDir = Path.Combine(outputDir, KmlBuilder.SanitizeFolderName(result.TripName));
        var files = KmlBuilder.SaveKmlFiles(result.KmlFiles, tripDir);

        progress?.Invoke("Done", 1.0);
        return result with { Files = files, OutputDir = tripDir };
    }

    private async Task<string> ConfirmNameAsync(
        System.Text.Json.Nodes.JsonObject document, string fileName,
        Func<string, Task<string>> confirm, CancellationToken ct)
    {
        var suggested = await gemini.SuggestTripNameAsync(document, fileName, ct);
        return await confirm(suggested);
    }
}
