using System.Text.Json;
using GmapPlanner.Core.Json;

namespace GmapPlanner.Core.Services;

/// <summary>Settings saved locally (ports appconfig.py's config.json).</summary>
public record AppSettings
{
    public string GoogleApiKey { get; set; } = "";
    public string GeoApiKey { get; set; } = "";
    public string OutputDir { get; set; } = "";

    /// <summary>Service-account JSON, only used for the live geocoding-usage gauge.</summary>
    public string GcpSaJson { get; set; } = "";
}

public static class AppSettingsService
{
    private static string ConfigPath => AppDataPaths.DataPath("config.json");

    public static AppSettings Load()
    {
        try
        {
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize(json, GmapPlannerJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, GmapPlannerJsonContext.Default.AppSettings);
        File.WriteAllText(ConfigPath, json);
    }
}
