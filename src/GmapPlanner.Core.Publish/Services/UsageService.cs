using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Google.Apis.Auth.OAuth2;
using GmapPlanner.Core.Json;

namespace GmapPlanner.Core.Services;

/// <summary>
/// Ports gmap_planner/usage.py: reads the real Places API (New) request count for the current
/// month from Cloud Monitoring and turns it into a percent-of-quota gauge. Everything is
/// best-effort — any failure (no service account, Monitoring API off, network) returns
/// null so the UI hides the gauge instead of erroring.
/// </summary>
public class UsageService(HttpClient http)
{
    // Google quotas reset on Pacific time, so month windows align to Los Angeles.
    private static readonly TimeZoneInfo Pacific = ResolvePacific();

    private static TimeZoneInfo ResolvePacific()
    {
        foreach (var id in new[] { "America/Los_Angeles", "Pacific Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); } catch { /* try next */ }
        }
        return TimeZoneInfo.Utc; // last resort; the gauge is best-effort anyway
    }

    /// <summary>Places API usage this month, or null if it can't be read.</summary>
    public async Task<UsageGauge?> GetGeocodeUsageAsync(
        string serviceAccountJson, int monthlyLimit = AppConfig.GeoMonthlyLimit, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(serviceAccountJson)) return null;

        var projectId = ProjectIdOf(serviceAccountJson);
        if (string.IsNullOrEmpty(projectId)) return null;

        try
        {
            var credential = GoogleCredential.FromJson(serviceAccountJson)
                .CreateScoped(AppConfig.MonitoringScope);
            var token = await ((ITokenAccess)credential).GetAccessTokenForRequestAsync(cancellationToken: ct);

            var now = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, Pacific);
            var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset);

            var used = await RequestCountAsync(projectId, token, AppConfig.GeocodeService, monthStart, now, ct);

            // Quota resets 1st of next month, 00:00 Pacific; count whole calendar days.
            var nextMonth = monthStart.AddMonths(1);
            var resetDays = (nextMonth.Date - now.Date).Days;

            var pct = monthlyLimit <= 0 ? 0.0 : Math.Clamp(used / (double)monthlyLimit * 100.0, 0.0, 100.0);
            return new UsageGauge(used, monthlyLimit, pct, resetDays);
        }
        catch
        {
            return null;
        }
    }

    private async Task<int> RequestCountAsync(
        string project, string token, string service,
        DateTimeOffset start, DateTimeOffset end, CancellationToken ct)
    {
        var windowSeconds = Math.Max((int)(end - start).TotalSeconds, 60);
        var filter =
            "metric.type=\"serviceruntime.googleapis.com/api/request_count\" " +
            "AND resource.type=\"consumed_api\" " +
            $"AND resource.label.\"service\"=\"{service}\"";
        var query =
            $"?filter={Uri.EscapeDataString(filter)}" +
            $"&interval.startTime={Uri.EscapeDataString(Rfc3339(start))}" +
            $"&interval.endTime={Uri.EscapeDataString(Rfc3339(end))}" +
            $"&aggregation.alignmentPeriod={windowSeconds}s" +
            "&aggregation.perSeriesAligner=ALIGN_SUM" +
            "&aggregation.crossSeriesReducer=REDUCE_SUM";
        var url = string.Format(AppConfig.MonitoringTimeSeriesUrl, project) + query;

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Authorization", $"Bearer {token}");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(15));
        var response = await http.SendAsync(request, cts.Token);
        response.EnsureSuccessStatusCode();

        var payload = await response.Content.ReadFromJsonAsync(
            PublishJsonContext.Default.TimeSeriesResponse, cts.Token);

        var total = 0.0;
        foreach (var series in payload?.TimeSeries ?? [])
            foreach (var point in series.Points ?? [])
            {
                var v = point.Value;
                if (v is null) continue;
                // int64Value arrives as a JSON string; doubleValue as a number.
                if (v.Int64Value is { Length: > 0 } s && long.TryParse(s, out var i)) total += i;
                else if (v.DoubleValue is { } d) total += d;
            }
        return (int)total;
    }

    /// <summary>The project id inside a service-account JSON, or null.</summary>
    public static string? ProjectIdOf(string serviceAccountJson)
    {
        try
        {
            return JsonNode.Parse(serviceAccountJson)?["project_id"]?.GetValue<string>();
        }
        catch
        {
            return null;
        }
    }

    private static string Rfc3339(DateTimeOffset dt) =>
        dt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}

// --- Cloud Monitoring timeSeries response DTOs ---

internal record TimeSeriesResponse
{
    [JsonPropertyName("timeSeries")] public List<TimeSeriesDto>? TimeSeries { get; init; }
}

internal record TimeSeriesDto
{
    [JsonPropertyName("points")] public List<PointDto>? Points { get; init; }
}

internal record PointDto
{
    [JsonPropertyName("value")] public TypedValueDto? Value { get; init; }
}

internal record TypedValueDto
{
    [JsonPropertyName("int64Value")] public string? Int64Value { get; init; }
    [JsonPropertyName("doubleValue")] public double? DoubleValue { get; init; }
}
