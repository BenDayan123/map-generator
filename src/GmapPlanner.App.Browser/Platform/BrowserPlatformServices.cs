using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using System.Text.Json;
using Avalonia.Platform.Storage;
using GmapPlanner.App.Browser;
using GmapPlanner.Core.Json;
using GmapPlanner.Core.Services;

namespace GmapPlanner.App.Platform;

/// <summary>
/// The browser host. Settings live in this browser's localStorage; KML files are handed over
/// as downloads. Publishing, Google login, usage/analytics and updates are off (the view hides
/// them) until phases 3–4 route them through the Vercel API.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed partial class BrowserPlatformServices : IPlatformServices
{
    private const string SettingsKey = "gmapplanner.settings";
    private const string SessionKey = "gmapplanner.session";
    private const string KmlMimeType = "application/vnd.google-earth.kml+xml";

    // Origin() is a JS interop call, so the HttpClient/BrowserApi are built lazily on first
    // use rather than in a field initializer (which could run before the runtime is ready).
    private BrowserApi? _api;
    // 20s cap so a stuck/misconfigured request errors to a clear message instead of spinning
    // the default 100s (the functions themselves cap at Vercel's max duration anyway).
    private BrowserApi Api => _api ??= new BrowserApi(new HttpClient { BaseAddress = new Uri(Origin()), Timeout = TimeSpan.FromSeconds(20) });

    public PlatformFeatures Features { get; } = new(
        Publish: true, Analytics: true, Updates: false, InlineFiles: true, MaxUploadMb: 14, RequiresSession: true);

    public AppSettings LoadSettings() => AppSettingsService.FromJson(GetItem(SettingsKey));

    public void SaveSettings(AppSettings settings) => SetItem(SettingsKey, AppSettingsService.ToJson(settings));

    public SetupStatus GetSetupStatus() => new(HasGoogleLogin: false, HasDriveCredentials: false, HasDriveToken: false);

    public void SaveDriveCredentials(string json) =>
        throw new NotSupportedException("Drive credentials aren't used by the web version yet.");

    public void OpenUrl(string url) => OpenUrlJs(url);

    public string? LoadSession()
    {
        var s = GetItem(SessionKey);
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    public void SaveSession(string sessionJson) => SetItem(SessionKey, sessionJson);

    public Task<string> SaveKmlFilesAsync(IStorageProvider storage, IReadOnlyList<KmlFile> files)
    {
        foreach (var file in files) DownloadText(file.FileName, file.Content, KmlMimeType);
        return Task.FromResult($"Downloaded {files.Count} file(s).");
    }

    public Task<UsageGauge?> GetUsageAsync(string serviceAccountJson) => Api.GetUsageAsync(serviceAccountJson);

    public Task<IReadOnlyList<AnalyticsRow>?> FetchAnalyticsAsync(string serviceAccountJson, string sheetId) =>
        Api.FetchAnalyticsAsync(serviceAccountJson, sheetId);

    public Task RecordTripAsync(string serviceAccountJson, string sheetId, string tripName, int maps, int places, IReadOnlyList<string> mapLinks) =>
        Task.CompletedTask;

    public async Task<IReadOnlyList<MapResult>> PublishAsync(
        string tripName, IReadOnlyList<KmlFile> files, IReadOnlyList<string> recipients,
        string role, bool notify, bool showBrowser, ProgressCallback progress)
    {
        var sessionJson = LoadSession()
            ?? throw new InvalidOperationException(
                "Load your session.json on the Settings page first (run the login helper to create it).");
        JsonElement session;
        try { session = JsonDocument.Parse(sessionJson).RootElement.Clone(); }
        catch { throw new InvalidOperationException("The saved session.json isn't valid JSON — load it again."); }

        // The SA JSON / Sheet id ride along so the worker can log the run (optional).
        var settings = LoadSettings();
        JsonElement? saJson = TryParse(settings.GcpSaJson);
        var sheetId = string.IsNullOrWhiteSpace(settings.AnalyticsSheetId) ? null : settings.AnalyticsSheetId;

        var req = new JobSubmitRequest(
            tripName,
            files.Select(f => new JobKmlDto(f.FileName, f.Content)).ToList(),
            recipients, role, notify, session, saJson, sheetId);

        progress("Submitting the publish job…", 0.05);
        var id = await Api.SubmitJobAsync(req);

        // Poll to a terminal state. 10 min cap; a stalled worker never hangs the UI forever.
        var deadline = DateTime.UtcNow.AddMinutes(10);
        JobPollResponse? status = null;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(5000);
            var s = await Api.PollJobAsync(id);
            if (s is null) continue;
            status = s;
            if (!string.IsNullOrEmpty(s.Message)) progress(s.Message!, 0.5);
            if (s.State is "done" or "failed") break;
        }
        if (status is null || status.State is not ("done" or "failed"))
            throw new InvalidOperationException("Publishing timed out — check the worker run and try again.");

        // Save the rotated session back so the next publish works without re-login.
        if (status.RefreshedSession is { } refreshed)
            SaveSession(refreshed.GetRawText());

        if (status.State == "failed")
            throw new InvalidOperationException(status.ErrorCode == "SESSION_EXPIRED"
                ? "Your saved Google session expired — run the login helper again and reload session.json."
                : status.Error ?? "Publishing failed.");

        return (status.Maps ?? [])
            .Select(m => new MapResult(m.FileName, m.Url, m.SharedWith, m.Error))
            .ToList();
    }

    private static JsonElement? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonDocument.Parse(json).RootElement.Clone(); }
        catch { return null; }
    }

    public Task LoginAsync() =>
        throw new NotSupportedException("The web version publishes with a session.json from the login helper.");

    public Task<bool> IsLoggedInAsync() => Task.FromResult(false);

    public Task<UpdateInfo?> CheckForUpdateAsync() => Task.FromResult<UpdateInfo?>(null);

    public Task InstallUpdateAsync(UpdateInfo update, Action<double> progress) =>
        throw new NotSupportedException("The web version updates itself on reload.");

    // Helpers defined on globalThis.gmapPlanner in wwwroot/main.js.
    [JSImport("globalThis.gmapPlanner.getItem")]
    private static partial string? GetItem(string key);

    [JSImport("globalThis.gmapPlanner.setItem")]
    private static partial void SetItem(string key, string value);

    [JSImport("globalThis.gmapPlanner.openUrl")]
    private static partial void OpenUrlJs(string url);

    [JSImport("globalThis.gmapPlanner.downloadText")]
    private static partial void DownloadText(string fileName, string content, string mimeType);

    [JSImport("globalThis.gmapPlanner.origin")]
    private static partial string Origin();
}
