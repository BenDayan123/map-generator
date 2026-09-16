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

    /// <summary>Submits a publish job; returns its id. Throws with the server's message on failure (surfaced in the banner).</summary>
    public async Task<string> SubmitJobAsync(JobSubmitRequest req, CancellationToken ct = default)
    {
        using var res = await _http.PostAsJsonAsync("api/jobs", req, GmapPlannerJsonContext.Default.JobSubmitRequest, ct);
        if (!res.IsSuccessStatusCode)
            throw new InvalidOperationException(await ErrorMessageAsync(res, ct));
        var dto = await res.Content.ReadFromJsonAsync(GmapPlannerJsonContext.Default.JobSubmitResponse, ct);
        return dto?.Id ?? throw new InvalidOperationException("The server didn't return a job id.");
    }

    /// <summary>Polls a job's status. Returns null on a transient read failure (the caller keeps polling).</summary>
    public async Task<JobPollResponse?> PollJobAsync(string id, CancellationToken ct = default)
    {
        try
        {
            using var res = await _http.GetAsync($"api/jobs/{id}", ct);
            if (!res.IsSuccessStatusCode) return null;
            return await res.Content.ReadFromJsonAsync(GmapPlannerJsonContext.Default.JobPollResponse, ct);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> ErrorMessageAsync(HttpResponseMessage res, CancellationToken ct)
    {
        try
        {
            var body = await res.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String)
                return $"Publish request failed: {err.GetString()}";
        }
        catch
        {
            // Fall through to the status-code message.
        }
        return $"Publish request failed ({(int)res.StatusCode}).";
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
