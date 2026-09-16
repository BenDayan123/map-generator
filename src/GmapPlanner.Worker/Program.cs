using System.Text.RegularExpressions;
using GmapPlanner.Core.Services;
using GmapPlanner.Core.Services.Publish;
using GmapPlanner.Worker;

// One publish job on a runner: claim -> materialize inputs -> publish via storage-state session
// -> share (Drive) -> log (Sheets) -> post status with map links + refreshed session.
var jobId = Environment.GetEnvironmentVariable("JOB_ID");
var apiBase = Environment.GetEnvironmentVariable("API_BASE_URL");
var hmac = Environment.GetEnvironmentVariable("WORKER_HMAC");
if (string.IsNullOrWhiteSpace(jobId) || string.IsNullOrWhiteSpace(apiBase) || string.IsNullOrWhiteSpace(hmac))
{
    Console.Error.WriteLine("JOB_ID, API_BASE_URL and WORKER_HMAC are required.");
    return 2;
}

var api = new WorkerApi(apiBase, hmac, jobId);
var tempDir = Path.Combine(Path.GetTempPath(), "gmap-job-" + jobId);
Directory.CreateDirectory(tempDir);

try
{
    var payload = await api.ClaimAsync();
    await api.PostStatusAsync(new JobStatus("running", Message: "starting"));

    // Materialize the Drive OAuth client + cached token and the KML files onto disk.
    var credentialsPath = Path.Combine(tempDir, "credentials.json");
    var tokenDir = Path.Combine(tempDir, "drive-token");
    SessionMaterializer.WriteDriveCreds(payload.Session, credentialsPath, tokenDir);

    var kmlPaths = new List<string>();
    foreach (var k in payload.Kmls)
    {
        var path = Path.Combine(tempDir, k.FileName);
        await File.WriteAllTextAsync(path, k.Content);
        kmlPaths.Add(path);
    }

    // Drive is optional when there are no recipients (we still restrict downloads when it's available).
    DriveShareService? drive = null;
    try
    {
        drive = await DriveShareService.CreateAsync(credentialsPath, tokenDir);
    }
    catch (DriveShareException) when (payload.Recipients.Count == 0)
    {
        drive = null;
    }

    await using var session = await MyMapsSession.StartFromStorageStateAsync(
        payload.Session.StorageState, headless: true, log: Console.WriteLine);

    // A re-challenged replayed session fails the whole job cleanly (never a bare timeout mid-batch).
    if (!await session.IsLoggedInAsync())
    {
        await api.PostStatusAsync(new JobStatus("failed",
            Error: "Your saved Google session was rejected — please run the login helper again.",
            ErrorCode: "SESSION_EXPIRED"));
        return 1;
    }

    var progress = new ProgressCallback((step, _) =>
        api.PostStatusAsync(new JobStatus("running", Message: step)).GetAwaiter().GetResult());

    var maps = await PublishService.PublishWithAsync(
        session, drive, kmlPaths, payload.TripName, payload.Recipients,
        role: payload.Role, notify: payload.Notify, progress: progress);

    // Return every map (including per-file failures) so the browser can fill its result rows.
    var mapDtos = maps
        .Select(m => new JobMap(Path.GetFileName(m.File), m.Title, m.ViewUrl, m.SharedWith, m.Error))
        .ToList();
    var successful = maps.Where(m => string.IsNullOrEmpty(m.Error)).ToList();

    // Analytics is best-effort — logging must never fail a publish.
    if (payload.SaJson is { } sa && !string.IsNullOrWhiteSpace(payload.SheetId))
    {
        try
        {
            var places = payload.Kmls.Sum(k => Regex.Matches(k.Content, "<Placemark").Count);
            using var http = new HttpClient();
            await new SheetsAnalyticsService(http).RecordPublishAsync(
                sa.GetRawText(), payload.SheetId!, payload.TripName,
                successful.Count, places, successful.Select(m => m.ViewUrl).ToList());
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"analytics logging failed (ignored): {e.Message}");
        }
    }

    var refreshed = await session.ExportStorageStateAsync();
    await api.PostStatusAsync(new JobStatus(
        State: successful.Count == 0 && maps.Count > 0 ? "failed" : "done",
        Message: $"created {successful.Count} map(s)",
        Maps: mapDtos,
        Error: successful.Count == 0 ? maps.FirstOrDefault()?.Error : null,
        RefreshedSession: refreshed));
    return 0;
}
catch (Exception e)
{
    // Setup/claim failures (and anything unexpected) surface as a failed job, not a silent crash.
    var code = (e as MyMapsException)?.ErrorCode;
    await api.PostStatusAsync(new JobStatus("failed", Error: e.Message, ErrorCode: code));
    Console.Error.WriteLine(e);
    return 1;
}
finally
{
    try { Directory.Delete(tempDir, recursive: true); } catch { /* best effort */ }
}
