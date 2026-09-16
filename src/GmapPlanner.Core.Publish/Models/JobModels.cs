using System.Text.Json;

namespace GmapPlanner.Core.Services.Publish;

/// <summary>
/// <c>session.json</c> the LoginHelper produces and the Worker consumes. The Google blobs
/// (<see cref="StorageState"/>, <see cref="DriveCredentials"/>, <see cref="DriveToken"/>) are kept
/// opaque as <see cref="JsonElement"/> so we never reflect over Google's shapes (trimming rule #1).
/// </summary>
public sealed record SessionFile(
    int Version,
    JsonElement StorageState,
    JsonElement DriveCredentials,
    JsonElement DriveToken);

/// <summary>A decrypted publish job the Worker runs (browser → <c>POST /api/jobs</c> → blob → Worker).</summary>
public sealed record JobPayload(
    string TripName,
    IReadOnlyList<KmlFile> Kmls,
    IReadOnlyList<string> Recipients,
    string Role,
    bool Notify,
    SessionFile Session,
    JsonElement? SaJson,
    string? SheetId);

/// <summary>One created My Maps map link.</summary>
public sealed record JobMap(string Title, string Url);

/// <summary>
/// Status the Worker posts and the browser polls. <see cref="RefreshedSession"/> carries the rotated
/// storage state back so a second publish works without re-login; <see cref="ErrorCode"/> is
/// <c>SESSION_EXPIRED</c> when the replayed Google session is re-challenged.
/// </summary>
public sealed record JobStatus(
    string State,
    string? Message = null,
    IReadOnlyList<JobMap>? Maps = null,
    string? Error = null,
    string? ErrorCode = null,
    JsonElement? RefreshedSession = null);
