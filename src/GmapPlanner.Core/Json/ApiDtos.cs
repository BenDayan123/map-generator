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

// --- Cloud publish jobs (phase 4): browser <-> /api/jobs. The browser can't reference
// Core.Publish, so these mirror JobPayload/JobStatus; camelCase matches the worker's context.
public sealed record JobKmlDto(
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("content")] string Content);

public sealed record JobSubmitRequest(
    [property: JsonPropertyName("tripName")] string TripName,
    [property: JsonPropertyName("kmls")] IReadOnlyList<JobKmlDto> Kmls,
    [property: JsonPropertyName("recipients")] IReadOnlyList<string> Recipients,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("notify")] bool Notify,
    [property: JsonPropertyName("session")] JsonElement Session,
    [property: JsonPropertyName("saJson")] JsonElement? SaJson,
    [property: JsonPropertyName("sheetId")] string? SheetId);

public sealed record JobSubmitResponse([property: JsonPropertyName("id")] string Id);

public sealed record JobMapDto(
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("sharedWith")] IReadOnlyList<string> SharedWith,
    [property: JsonPropertyName("error")] string Error);

public sealed record JobPollResponse(
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("message")] string? Message,
    [property: JsonPropertyName("maps")] IReadOnlyList<JobMapDto>? Maps,
    [property: JsonPropertyName("error")] string? Error,
    [property: JsonPropertyName("errorCode")] string? ErrorCode,
    [property: JsonPropertyName("refreshedSession")] JsonElement? RefreshedSession);
