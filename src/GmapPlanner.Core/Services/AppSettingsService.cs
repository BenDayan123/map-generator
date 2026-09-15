using System.Text.Json;
using GmapPlanner.Core.Json;

namespace GmapPlanner.Core.Services;

/// <summary>Settings saved locally (ports appconfig.py's config.json).</summary>
public record AppSettings
{
    public string GoogleApiKey { get; set; } = "";
    public string GeoApiKey { get; set; } = "";
    public string OutputDir { get; set; } = "";

    /// <summary>Service-account JSON, used for the live geocoding-usage gauge and the analytics Sheet.</summary>
    public string GcpSaJson { get; set; } = "";

    /// <summary>Id (or full URL) of the Google Sheet the Analytics page logs to and reads from.</summary>
    public string AnalyticsSheetId { get; set; } = "";
}

public static class AppSettingsService
{
    private static string ConfigPath => AppDataPaths.DataPath("config.json");

    public static AppSettings Load()
    {
        try
        {
            return FromJson(File.ReadAllText(ConfigPath));
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings) => File.WriteAllText(ConfigPath, ToJson(settings));

    /// <summary>Settings as JSON (source-generated; the browser host keeps this in localStorage).</summary>
    public static string ToJson(AppSettings settings) =>
        JsonSerializer.Serialize(settings, GmapPlannerJsonContext.Default.AppSettings);

    /// <summary>Parses settings JSON; null, blank or invalid JSON gives defaults.</summary>
    public static AppSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new AppSettings();
        try
        {
            return JsonSerializer.Deserialize(json, GmapPlannerJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }
}
