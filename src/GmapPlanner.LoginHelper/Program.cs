using System.Text.Json;
using GmapPlanner.Core.Json;
using GmapPlanner.Core.Services.Publish;

// Interactive tool: captures the two things the cloud worker needs to publish on the user's
// behalf — a signed-in My Maps browser session and a Drive OAuth token — into session.json.
// Usage: GmapPlanner.LoginHelper [path-to-credentials.json] [output-dir]
var credentialsPath = args.Length > 0 ? args[0] : Path.Combine(Directory.GetCurrentDirectory(), "credentials.json");
var outDir = args.Length > 1 ? args[1] : Directory.GetCurrentDirectory();

if (!File.Exists(credentialsPath))
{
    Console.Error.WriteLine(
        $"Drive OAuth client not found at '{credentialsPath}'.\n" +
        "Create an OAuth 2.0 Desktop client in Google Cloud Console (Drive API enabled), " +
        "download it as credentials.json, and pass its path as the first argument.");
    return 2;
}

try
{
    Console.WriteLine("Step 1/2 — a browser window will open. Sign in to Google My Maps, then wait…");
    var storageState = await MyMapsSession.LoginAndCaptureStorageStateAsync(log: Console.WriteLine);
    Console.WriteLine("  My Maps session captured.");

    Console.WriteLine("Step 2/2 — approve Google Drive access in the browser…");
    var tokenDir = Path.Combine(Path.GetTempPath(), "gmap-login-token-" + Guid.NewGuid().ToString("N"));
    // CreateAsync runs the OAuth consent and caches the token in tokenDir (its return isn't needed here).
    _ = await DriveShareService.CreateAsync(credentialsPath, tokenDir);
    var driveToken = SessionMaterializer.CaptureDriveToken(tokenDir);
    var driveCredentials = JsonDocument.Parse(await File.ReadAllTextAsync(credentialsPath)).RootElement.Clone();
    try { Directory.Delete(tokenDir, recursive: true); } catch { /* best effort */ }

    var session = new SessionFile(1, storageState, driveCredentials, driveToken);
    var outPath = Path.Combine(outDir, "session.json");
    await File.WriteAllTextAsync(outPath, JsonSerializer.Serialize(session, PublishJsonContext.Default.SessionFile));

    Console.WriteLine($"\nDone. Wrote {outPath}");
    Console.WriteLine("Load it on the web app's Settings page to enable cloud publishing.");
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"Login helper failed: {e.Message}");
    return 1;
}
