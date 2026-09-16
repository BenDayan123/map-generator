using Avalonia.Platform.Storage;
using GmapPlanner.Core.Services;

namespace GmapPlanner.App.Platform;

/// <summary>What a host can do. The view hides everything a host can't.</summary>
/// <param name="Publish">My Maps publishing, Google login, Drive credentials.</param>
/// <param name="Analytics">Usage ring + Analytics Sheet (both need the service-account JSON).</param>
/// <param name="Updates">The in-app updater card.</param>
/// <param name="InlineFiles">Send PDFs to Gemini inline (the browser can't use the upload flow).</param>
/// <param name="MaxUploadMb">Largest itinerary accepted.</param>
/// <param name="RequiresSession">Publishing needs a loaded session.json (the cloud/browser host).</param>
public sealed record PlatformFeatures(bool Publish, bool Analytics, bool Updates, bool InlineFiles, int MaxUploadMb, bool RequiresSession = false);

/// <summary>One published map, host-agnostic.</summary>
public sealed record MapResult(string FileName, string ViewUrl, IReadOnlyList<string> SharedWith, string Error);

/// <summary>The login/Drive rows of the Settings checklist.</summary>
public sealed record SetupStatus(bool HasGoogleLogin, bool HasDriveCredentials, bool HasDriveToken);

/// <summary>
/// Everything host-specific the view model needs. Two implementations: the desktop exe
/// (files, Playwright, Drive, Sheets, Monitoring, updater) and the browser (localStorage,
/// downloads; phases 3–4 fill in analytics and publishing through the Vercel API).
/// </summary>
public interface IPlatformServices
{
    PlatformFeatures Features { get; }

    AppSettings LoadSettings();
    void SaveSettings(AppSettings settings);
    SetupStatus GetSetupStatus();
    void SaveDriveCredentials(string json);

    void OpenUrl(string url);

    /// <summary>The saved cloud-publish session.json (opaque JSON), or null. Browser host only; desktop returns null.</summary>
    string? LoadSession();

    /// <summary>Persists the cloud-publish session.json. Browser host only; desktop is a no-op.</summary>
    void SaveSession(string sessionJson);

    /// <summary>Hands the KML files to the user. Returns a status line, or "" if the user cancelled.</summary>
    Task<string> SaveKmlFilesAsync(IStorageProvider storage, IReadOnlyList<KmlFile> files);

    Task<UsageGauge?> GetUsageAsync(string serviceAccountJson);
    Task<IReadOnlyList<AnalyticsRow>?> FetchAnalyticsAsync(string serviceAccountJson, string sheetId);
    Task RecordTripAsync(string serviceAccountJson, string sheetId, string tripName, int maps, int places, IReadOnlyList<string> mapLinks);

    /// <summary>One map per KML file. Throws for a setup/auth failure; per-file failures come back in <see cref="MapResult.Error"/>.</summary>
    Task<IReadOnlyList<MapResult>> PublishAsync(
        string tripName, IReadOnlyList<KmlFile> files, IReadOnlyList<string> recipients,
        string role, bool notify, bool showBrowser, ProgressCallback progress);

    Task LoginAsync();
    Task<bool> IsLoggedInAsync();

    Task<UpdateInfo?> CheckForUpdateAsync();

    /// <summary>Downloads and starts the installer (on Windows this exits the app).</summary>
    Task InstallUpdateAsync(UpdateInfo update, Action<double> progress);
}
