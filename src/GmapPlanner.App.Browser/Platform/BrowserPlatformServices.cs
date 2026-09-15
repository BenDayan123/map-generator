using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Avalonia.Platform.Storage;
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
    private const string KmlMimeType = "application/vnd.google-earth.kml+xml";

    public PlatformFeatures Features { get; } = new(Publish: false, Analytics: false, Updates: false, InlineFiles: true, MaxUploadMb: 14);

    public AppSettings LoadSettings() => AppSettingsService.FromJson(GetItem(SettingsKey));

    public void SaveSettings(AppSettings settings) => SetItem(SettingsKey, AppSettingsService.ToJson(settings));

    public SetupStatus GetSetupStatus() => new(HasGoogleLogin: false, HasDriveCredentials: false, HasDriveToken: false);

    public void SaveDriveCredentials(string json) =>
        throw new NotSupportedException("Drive credentials aren't used by the web version yet.");

    public void OpenUrl(string url) => OpenUrlJs(url);

    public Task<string> SaveKmlFilesAsync(IStorageProvider storage, IReadOnlyList<KmlFile> files)
    {
        foreach (var file in files) DownloadText(file.FileName, file.Content, KmlMimeType);
        return Task.FromResult($"Downloaded {files.Count} file(s).");
    }

    public Task<UsageGauge?> GetUsageAsync(string serviceAccountJson) => Task.FromResult<UsageGauge?>(null);

    public Task<IReadOnlyList<AnalyticsRow>?> FetchAnalyticsAsync(string serviceAccountJson, string sheetId) =>
        Task.FromResult<IReadOnlyList<AnalyticsRow>?>(null);

    public Task RecordTripAsync(string serviceAccountJson, string sheetId, string tripName, int maps, int places, IReadOnlyList<string> mapLinks) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<MapResult>> PublishAsync(
        string tripName, IReadOnlyList<KmlFile> files, IReadOnlyList<string> recipients,
        string role, bool notify, bool showBrowser, ProgressCallback progress) =>
        throw new NotSupportedException("Publishing to My Maps isn't available in the web version yet.");

    public Task LoginAsync() =>
        throw new NotSupportedException("Google login isn't available in the web version yet.");

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
}
