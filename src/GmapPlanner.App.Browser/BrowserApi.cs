using System.Net.Http.Json;
using System.Text.Json;
using GmapPlanner.Core.Json;
using GmapPlanner.Core.Services;

namespace GmapPlanner.App.Browser;

/// <summary>Typed client for the same-origin Vercel API functions (/api/usage, /api/analytics).
/// Best-effort like the desktop services it stands in for: any failure or non-2xx response
/// returns null so the usage ring hides / the Analytics page shows "configure it" instead of
/// throwing. The SA JSON is a secret — it only ever goes in the POST body, never a URL.</summary>
internal sealed class BrowserApi
{
    private readonly HttpClient _http;

    public BrowserApi(HttpClient http) => _http = http;

    public async Task<UsageGauge?> GetUsageAsync(string saJson, CancellationToken ct = default)
    {
        try
        {
            var req = new UsageApiRequest(JsonDocument.Parse(saJson).RootElement.Clone());
            using var res = await _http.PostAsJsonAsync("api/usage", req, GmapPlannerJsonContext.Default.UsageApiRequest, ct);
            if (!res.IsSuccessStatusCode) return null;
            var dto = await res.Content.ReadFromJsonAsync(GmapPlannerJsonContext.Default.UsageApiResponse, ct);
            return dto is null ? null : new UsageGauge(dto.Used, dto.Limit, dto.Percent, dto.ResetDays);
        }
        catch { return null; }
    }

    public async Task<IReadOnlyList<AnalyticsRow>?> FetchAnalyticsAsync(string saJson, string sheetId, CancellationToken ct = default)
    {
        try
        {
            var req = new AnalyticsApiRequest(JsonDocument.Parse(saJson).RootElement.Clone(), sheetId);
            using var res = await _http.PostAsJsonAsync("api/analytics", req, GmapPlannerJsonContext.Default.AnalyticsApiRequest, ct);
            if (!res.IsSuccessStatusCode) return null;
            var dto = await res.Content.ReadFromJsonAsync(GmapPlannerJsonContext.Default.AnalyticsApiResponse, ct);
            if (dto is null) return null;
            return dto.Rows.Select(r => new AnalyticsRow(r.CreatedAt, r.TripName, r.Maps, r.Places, r.Links)).ToList();
        }
        catch { return null; }
    }
}
