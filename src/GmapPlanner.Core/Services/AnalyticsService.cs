using System.Text.Json;
using GmapPlanner.Core.Json;

namespace GmapPlanner.Core.Services;

/// <summary>
/// One generated-trip record. Ports the columns the Python original's analytics.py logged
/// to a Google Sheet (created_at / trip_name / maps / places), kept local here instead —
/// this is a desktop app with no hosted Sheet to survive redeploys.
/// </summary>
public sealed class AnalyticsRecord
{
    public DateTime CreatedAt { get; set; }
    public string TripName { get; set; } = "";
    public int Days { get; set; }
    public int Locations { get; set; }
    public int ExactCoords { get; set; }
    public int Maps { get; set; }
}

/// <summary>Append-only local run log (analytics.json in the app data dir). Best-effort.</summary>
public static class AnalyticsService
{
    private static string LogPath => AppDataPaths.DataPath("analytics.json");

    public static List<AnalyticsRecord> Load()
    {
        try
        {
            var json = File.ReadAllText(LogPath);
            return JsonSerializer.Deserialize(json, GmapPlannerJsonContext.Default.ListAnalyticsRecord) ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static void Append(AnalyticsRecord record)
    {
        try
        {
            var all = Load();
            all.Add(record);
            var json = JsonSerializer.Serialize(all, GmapPlannerJsonContext.Default.ListAnalyticsRecord);
            File.WriteAllText(LogPath, json);
        }
        catch
        {
            // Logging must never break a run — a full disk or locked file is not fatal.
        }
    }
}
