namespace GmapPlanner.Core.Services;

/// <summary>One logged trip, read back from the analytics Sheet.</summary>
public sealed record AnalyticsRow(string CreatedAt, string TripName, int Maps, int Places, IReadOnlyList<string> MapLinks);

/// <summary>Analytics Sheet id handling shared by every host (no Google.Apis needed).</summary>
public static class AnalyticsSheet
{
    /// <summary>True when both the service account and a Sheet id are configured.</summary>
    public static bool IsConfigured(string saJson, string sheetId) =>
        !string.IsNullOrWhiteSpace(saJson) && !string.IsNullOrWhiteSpace(SheetIdOf(sheetId));

    /// <summary>The Sheet id, accepting either a bare id or a full spreadsheet URL.</summary>
    public static string SheetIdOf(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        raw = raw.Trim();
        const string marker = "/spreadsheets/d/";
        var i = raw.IndexOf(marker, StringComparison.Ordinal);
        return i < 0 ? raw : raw[(i + marker.Length)..].Split('/', 2)[0];
    }

    /// <summary>The shareable URL for a Sheet id (for the "view source" link).</summary>
    public static string SheetUrl(string sheetId) =>
        $"https://docs.google.com/spreadsheets/d/{SheetIdOf(sheetId)}";
}
