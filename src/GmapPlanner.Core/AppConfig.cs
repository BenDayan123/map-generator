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

    // --- My Maps browser automation + Drive sharing ---------------------------
    // My Maps has no create/import API, so a map is created by driving the editor.
    // Sharing then goes through the Drive API (a My Maps map is a Drive file).
    // /u/0/ pins the first signed-in account: without it a multi-account profile
    // can land on an account chooser instead of the map list.
    public const string MyMapsHomeUrl = "https://www.google.com/maps/d/u/0/?hl=en";
    public const string MyMapsMapMime = "application/vnd.google-apps.map";

    /// <summary>
    /// Sharing a map created in the browser (not by this app) needs full Drive scope;
    /// the narrower drive.file scope only covers files the app itself created.
    /// </summary>
    public static readonly string[] DriveScopes = ["https://www.googleapis.com/auth/drive"];

    /// <summary>Friendly share-role values → Drive permission roles.</summary>
    public static readonly Dictionary<string, string> DriveRoleAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["viewer"] = "reader", ["reader"] = "reader",
        ["commenter"] = "commenter", ["comment"] = "commenter",
        ["editor"] = "writer", ["writer"] = "writer",
    };

    /// <summary>
    /// Seconds between two map creations in one run. Creating maps back to back is
    /// what makes Google reject the second import with "Your action was reverted".
    /// </summary>
    public const int MapGapSeconds = 5;

    public static string PlaywrightProfileDir => AppDataPaths.DataPath(".pw-profile");
    public static string DriveCredentialsFile => AppDataPaths.DataPath("credentials.json");
    public static string DriveTokenDir => AppDataPaths.DataPath("drive-token");

    // --- Geocoding usage gauge (Cloud Monitoring) -----------------------------
    // Reads the real Geocoding request count for the month and shows it as
    // "percent of quota used". Needs a service account with roles/monitoring.viewer
    // and the Cloud Monitoring API enabled; degrades to hidden when not configured.
    public const int GeoMonthlyLimit = 10000;
    public const string MonitoringScope = "https://www.googleapis.com/auth/monitoring.read";
    public const string GeocodeService = "geocoding-backend.googleapis.com";

    /// <summary>Cloud Monitoring timeSeries endpoint; {0} is the GCP project id.</summary>
    public const string MonitoringTimeSeriesUrl =
        "https://monitoring.googleapis.com/v3/projects/{0}/timeSeries";
}
