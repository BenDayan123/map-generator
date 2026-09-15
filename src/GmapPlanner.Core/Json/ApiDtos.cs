using System.Text.Json;
using System.Text.Json.Serialization;

namespace GmapPlanner.Core.Json;

/// <summary>Request/response shapes for the browser host's same-origin Vercel API
/// (`/api/usage`, `/api/analytics`). camelCase on the wire; explicit names rather than a
/// context-wide naming policy so the existing DTOs in <see cref="GmapPlannerJsonContext"/>
/// (PascalCase) aren't affected.</summary>
public sealed record UsageApiRequest([property: JsonPropertyName("saJson")] JsonElement SaJson);

public sealed record UsageApiResponse(
    [property: JsonPropertyName("used")] int Used,
    [property: JsonPropertyName("limit")] int Limit,
    [property: JsonPropertyName("percent")] double Percent,
    [property: JsonPropertyName("resetDays")] int? ResetDays);

public sealed record AnalyticsApiRequest(
    [property: JsonPropertyName("saJson")] JsonElement SaJson,
    [property: JsonPropertyName("sheetId")] string SheetId);

public sealed record AnalyticsApiRow(
    [property: JsonPropertyName("createdAt")] string CreatedAt,
    [property: JsonPropertyName("tripName")] string TripName,
    [property: JsonPropertyName("maps")] int Maps,
    [property: JsonPropertyName("places")] int Places,
    [property: JsonPropertyName("links")] IReadOnlyList<string> Links);

public sealed record AnalyticsApiResponse([property: JsonPropertyName("rows")] IReadOnlyList<AnalyticsApiRow> Rows);
