namespace GmapPlanner.Core;

/// <summary>Shared constants (phase 1: extraction/geocoding/KML only).</summary>
public static class AppConfig
{
    public const string GeminiModel = "gemini-3.1-flash-lite";

    /// <summary>Google My Maps allows at most 10 layers per map (one KML file = one map).</summary>
    public const int MaxLayersPerFile = 10;

    public const string GeocodeUrl = "https://maps.googleapis.com/maps/api/geocode/json";

    /// <summary>Per-day pin colors (Material 700 shades; white number stays readable on each).</summary>
    public static readonly string[] DayColors =
    [
        "0288D1", "D32F2F", "388E3C", "7B1FA2", "E65100", "00796B",
        "C2185B", "303F9F", "5D4037", "455A64", "0097A7", "827717",
    ];
}
