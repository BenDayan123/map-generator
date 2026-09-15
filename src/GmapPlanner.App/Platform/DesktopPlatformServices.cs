using System.Diagnostics;
using Avalonia.Platform.Storage;
using GmapPlanner.Core;
using GmapPlanner.Core.Services;
using GmapPlanner.Core.Services.Publish;

namespace GmapPlanner.App.Platform;

/// <summary>The desktop host: config.json, a real browser for My Maps, Drive/Sheets/Monitoring, the updater.</summary>
public sealed class DesktopPlatformServices : IPlatformServices
{
    private static readonly HttpClient Http = new();
    private readonly UsageService _usage = new(Http);
    private readonly SheetsAnalyticsService _sheets = new(Http);
    private readonly UpdateService _updater = new(Http);

    // KML handed to Playwright is written here, never the user's Downloads.
    // ponytail: OS temp, no cleanup — files are small and Windows/macOS reclaim temp.
    private static string WorkDir => Path.Combine(Path.GetTempPath(), "GmapPlanner");

    public PlatformFeatures Features { get; } = new(Publish: true, Analytics: true, Updates: true, InlineFiles: false, MaxUploadMb: 15);

    public AppSettings LoadSettings() => AppSettingsService.Load();

    public void SaveSettings(AppSettings settings) => AppSettingsService.Save(settings);

    public SetupStatus GetSetupStatus() => new(
        DirHasFiles(AppConfig.PlaywrightProfileDir),
        File.Exists(AppConfig.DriveCredentialsFile),
        DirHasFiles(AppConfig.DriveTokenDir));

    public void SaveDriveCredentials(string json) => File.WriteAllText(AppConfig.DriveCredentialsFile, json);

    public void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* opening a browser is a nicety */ }
    }

    public async Task<string> SaveKmlFilesAsync(IStorageProvider storage, IReadOnlyList<KmlFile> files)
    {
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Save KML files to…",
            AllowMultiple = false,
        });
        var folder = folders.FirstOrDefault()?.TryGetLocalPath();
        if (folder is null) return "";

        KmlBuilder.SaveKmlFiles(files, folder);
        try
        {
            // Best-effort reveal, like the Python app's reveal_in_file_manager.
            var (exe, args) = OperatingSystem.IsMacOS() ? ("open", folder) : ("explorer.exe", folder);
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
        }
        catch
        {
            // Opening a file manager is a nicety; never fail over it.
        }
        return $"Saved {files.Count} file(s) to {folder}";
    }

    public Task<UsageGauge?> GetUsageAsync(string serviceAccountJson) => _usage.GetGeocodeUsageAsync(serviceAccountJson);

    public async Task<IReadOnlyList<AnalyticsRow>?> FetchAnalyticsAsync(string serviceAccountJson, string sheetId) =>
        await _sheets.FetchRowsAsync(serviceAccountJson, sheetId);

    public Task RecordTripAsync(string serviceAccountJson, string sheetId, string tripName, int maps, int places, IReadOnlyList<string> mapLinks) =>
        _sheets.RecordPublishAsync(serviceAccountJson, sheetId, tripName, maps, places, mapLinks);

    public async Task<IReadOnlyList<MapResult>> PublishAsync(
        string tripName, IReadOnlyList<KmlFile> files, IReadOnlyList<string> recipients,
        string role, bool notify, bool showBrowser, ProgressCallback progress)
    {
        // Playwright imports from disk, so the in-memory KML is written out first.
        var paths = KmlBuilder.SaveKmlFiles(files, Path.Combine(WorkDir, KmlBuilder.SanitizeFolderName(tripName)));
        var maps = await PublishService.PublishKmlFilesAsync(
            paths, tripName, recipients, role: role, headless: !showBrowser, notify: notify, progress: progress);
        return maps.Select(m => new MapResult(Path.GetFileName(m.File), m.ViewUrl, m.SharedWith, m.Error)).ToList();
    }

    public Task LoginAsync() => MyMapsSession.LoginAsync();

    public async Task<bool> IsLoggedInAsync()
    {
        await using var session = await MyMapsSession.StartAsync(headless: true);
        return await session.IsLoggedInAsync();
    }

    public Task<UpdateInfo?> CheckForUpdateAsync() => _updater.CheckForUpdateAsync(AppConfig.GithubRepo);

    public async Task InstallUpdateAsync(UpdateInfo update, Action<double> progress)
    {
        var path = await _updater.DownloadAssetAsync(update.AssetUrl, update.AssetName, progress: progress);
        // On Windows this quits the app so the installer can replace the files, then
        // relaunches the new version; on macOS it opens the .dmg for a drag-install.
        UpdateService.ApplyUpdate(path);
    }

    private static bool DirHasFiles(string path)
    {
        try { return Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any(); }
        catch { return false; }
    }
}
