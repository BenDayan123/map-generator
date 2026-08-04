using System.Text.Json;

namespace GmapPlanner.Core.Services;

/// <summary>Settings saved locally (phase 1 fields only; ports appconfig.py's config.json).</summary>
public record AppSettings
{
    public string GoogleApiKey { get; set; } = "";
    public string GeoApiKey { get; set; } = "";
    public string OutputDir { get; set; } = "";
}

public static class AppSettingsService
{
    private static string ConfigPath => AppDataPaths.DataPath("config.json");

    public static AppSettings Load()
    {
        try
        {
            var json = File.ReadAllText(ConfigPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(ConfigPath, json);
    }
}
