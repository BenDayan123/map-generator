using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GmapPlanner.App.Platform;
using GmapPlanner.App.Localization;
using GmapPlanner.Core;
using GmapPlanner.Core.Services;
using GmapPlanner.Core.Services.Gemini;

namespace GmapPlanner.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private static readonly HttpClient Http = new();

    private readonly IPlatformServices _platform;

    public int MaxLayersPerFile => AppConfig.MaxLayersPerFile;

    /// <summary>What this host supports; the view binds visibility to it.</summary>
    public PlatformFeatures Features => _platform.Features;

    /// <summary>Drop-zone caption; the size cap follows the host (15 MB desktop, 14 MB browser).</summary>
    public string UploadHint => Loc.F("OneFileLimit", Features.MaxUploadMb);

    // --- Navigation ---------------------------------------------------------
    public enum AppPage { MakeMap, Analytics, Settings }

    [ObservableProperty] private AppPage _page = AppPage.MakeMap;

    // --- Language -----------------------------------------------------------
    // Hebrew flips the whole window right-to-left; strings come from Localization/Strings.cs.
    [ObservableProperty] private bool _isHebrew;
    public FlowDirection AppFlowDirection => IsHebrew ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

    [RelayCommand]
    private void ToggleLanguage() => IsHebrew = !IsHebrew;

    partial void OnIsHebrewChanged(bool value)
    {
        Loc.SetHebrew(value);
        OnPropertyChanged(nameof(AppFlowDirection));
        OnPropertyChanged(nameof(UploadHint));
        FormatUsageText();
        SaveSettings();
    }
    public bool IsMakeMapPage => Page == AppPage.MakeMap;
    public bool IsAnalyticsPage => Page == AppPage.Analytics;
    public bool IsSettingsPage => Page == AppPage.Settings;

    partial void OnPageChanged(AppPage value)
    {
        OnPropertyChanged(nameof(IsMakeMapPage));
        OnPropertyChanged(nameof(IsAnalyticsPage));
        OnPropertyChanged(nameof(IsSettingsPage));
        if (value == AppPage.Analytics) _ = LoadAnalyticsAsync();
    }

    // --- Input + settings ---------------------------------------------------
    private byte[]? _inputContent;
    [ObservableProperty] private string _inputFileName = "";
    [ObservableProperty] private string _googleApiKey;
    [ObservableProperty] private string _geoApiKey;
    [ObservableProperty] private string _gcpSaJson;
    [ObservableProperty] private string _analyticsSheetId;
    [ObservableProperty] private string _setupMessage = "";

    // --- Setup status (green/⚪ checklist) -----------------------------------
    [ObservableProperty] private bool _hasGeminiKey;
    [ObservableProperty] private bool _hasGeoKey;
    [ObservableProperty] private bool _hasGoogleLogin;
    [ObservableProperty] private bool _hasDriveCredentials;
    [ObservableProperty] private bool _hasDriveToken;

    // --- Usage gauge --------------------------------------------------------
    private bool _usageLoading;
    private UsageGauge? _lastGauge;
    [ObservableProperty] private bool _hasUsage;
    [ObservableProperty] private Geometry? _usageRingGeometry;
    [ObservableProperty] private string _usageColor = "#388E3C";
    [ObservableProperty] private string _usagePercentText = "";
    [ObservableProperty] private string _usageSubText = "";

    // --- Options (the Streamlit sidebar) ------------------------------------
    [ObservableProperty] private int _layersPerFile = AppConfig.MaxLayersPerFile;
    [ObservableProperty] private bool _skipGeocoding;

    // --- Publish to My Maps -------------------------------------------------
    [ObservableProperty] private bool _publishEnabled;
    [ObservableProperty] private int _shareRoleIndex; // 0 viewer, 1 commenter, 2 editor
    [ObservableProperty] private bool _notifyShare = true;
    [ObservableProperty] private bool _showBrowser;
    [ObservableProperty] private string _loginStatus = "";
    [ObservableProperty] private bool _isLoggingIn;

    // Cloud publish (browser): a session.json from the login helper, kept in localStorage.
    [ObservableProperty] private bool _hasPublishSession;
    [ObservableProperty] private string _sessionStatus = "";

    public string[] ShareRoles { get; } = ["viewer", "commenter", "editor"];

    private string SelectedRole =>
        ShareRoles[Math.Clamp(ShareRoleIndex, 0, ShareRoles.Length - 1)];

    /// <summary>Recipient email chips shown in the token input. Deduped on add.</summary>
    public ObservableCollection<string> ShareEmailsList { get; } = [];

    private static readonly char[] EmailSeparators = [',', ';', ' ', '\t', '\n', '\r'];

    /// <summary>Splits a typed/pasted string on the usual separators and adds each as a chip.</summary>
    public void AddEmails(string raw)
    {
        foreach (var email in raw.Split(EmailSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!ShareEmailsList.Any(x => string.Equals(x, email, StringComparison.OrdinalIgnoreCase)))
                ShareEmailsList.Add(email);
    }

    /// <summary>Backspace on an empty input pops the last chip.</summary>
    public void RemoveLastEmail()
    {
        if (ShareEmailsList.Count > 0) ShareEmailsList.RemoveAt(ShareEmailsList.Count - 1);
    }

    [RelayCommand]
    private void RemoveEmail(string? email)
    {
        if (email is not null) ShareEmailsList.Remove(email);
    }

    // --- Updates ------------------------------------------------------------
    private UpdateInfo? _pendingUpdate;

    public string AppVersion => $"v{UpdateService.CurrentVersion()}";
    [ObservableProperty] private string _updateStatus = "";
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private bool _isCheckingUpdate;

    partial void OnIsCheckingUpdateChanged(bool value)
    {
        CheckForUpdatesCommand.NotifyCanExecuteChanged();
        DownloadAndInstallUpdateCommand.NotifyCanExecuteChanged();
    }

    // --- Analytics (Google Sheet) -------------------------------------------
    [ObservableProperty] private bool _hasAnalytics;              // rows loaded and shown
    [ObservableProperty] private bool _analyticsLoading;
    [ObservableProperty] private string _analyticsMessage = "";   // empty-state / config / error text
    [ObservableProperty] private string _totalTrips = "0";
    [ObservableProperty] private string _totalMaps = "0";
    [ObservableProperty] private string _totalPlaces = "0";
    [ObservableProperty] private string _analyticsThisMonth = "";

    public bool HasAnalyticsSheetLink => AnalyticsSheet.IsConfigured(GcpSaJson, AnalyticsSheetId);
    public ObservableCollection<AnalyticsBar> AnalyticsBars { get; } = [];

    /// <summary>
    /// Loads the analytics page from the Google Sheet. Best-effort: an unconfigured or
    /// unreachable Sheet shows a guidance message rather than an error. Called on nav + after a run.
    /// </summary>
    private async Task LoadAnalyticsAsync()
    {
        OnPropertyChanged(nameof(HasAnalyticsSheetLink));
        if (!AnalyticsSheet.IsConfigured(GcpSaJson, AnalyticsSheetId))
        {
            HasAnalytics = false;
            AnalyticsMessage = Loc.T("AnNotConfigured");
            return;
        }

        AnalyticsLoading = true;
        AnalyticsMessage = Loc.T("AnLoading");
        try
        {
            var rows = await _platform.FetchAnalyticsAsync(GcpSaJson, AnalyticsSheetId);
            if (rows is null)
            {
                HasAnalytics = false;
                AnalyticsMessage = Loc.T("AnCantRead");
                return;
            }
            if (rows.Count == 0)
            {
                HasAnalytics = false;
                AnalyticsMessage = Loc.T("AnEmpty");
                return;
            }

            TotalTrips = rows.Select(r => r.TripName).Distinct(StringComparer.OrdinalIgnoreCase).Count().ToString();
            TotalMaps = rows.Sum(r => r.Maps).ToString();
            TotalPlaces = rows.Sum(r => r.Places).ToString();

            var monthPrefix = DateTime.Now.ToString("yyyy-MM");
            var monthRows = rows.Where(r => r.CreatedAt.StartsWith(monthPrefix)).ToList();
            AnalyticsThisMonth = Loc.F("AnThisMonth",
                monthRows.Count, monthRows.Sum(r => r.Maps), monthRows.Sum(r => r.Places));

            // Bar chart: places per trip for the most recent rows (newest at top).
            AnalyticsBars.Clear();
            var recent = rows.AsEnumerable().Reverse().Take(8).ToList();
            var max = Math.Max(1, recent.Max(r => r.Places));
            foreach (var r in recent)
                AnalyticsBars.Add(new AnalyticsBar
                {
                    Label = string.IsNullOrWhiteSpace(r.TripName) ? r.CreatedAt : r.TripName,
                    ValueText = r.Places.ToString(),
                    BarWidth = 20 + 240.0 * r.Places / max, // min stub so tiny values stay visible
                });

            HasAnalytics = true;
        }
        catch
        {
            HasAnalytics = false;
            AnalyticsMessage = Loc.T("AnLoadFailed");
        }
        finally
        {
            AnalyticsLoading = false;
        }
    }

    [RelayCommand]
    private void OpenAnalyticsSheet()
    {
        if (HasAnalyticsSheetLink) _platform.OpenUrl(AnalyticsSheet.SheetUrl(AnalyticsSheetId));
    }

    // --- Run state ----------------------------------------------------------
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private string _resultTripName = "";
    [ObservableProperty] private string _resultDays = "";
    [ObservableProperty] private string _resultLocations = "";
    [ObservableProperty] private string _resultExactCoords = "";
    [ObservableProperty] private string _geocodeWarning = "";

    public ObservableCollection<KmlFileItem> ResultFiles { get; } = [];

    // --- Trip-name prompt (side panel shown mid-run; KML writing waits on it) ----
    [ObservableProperty] private bool _isNamePromptOpen;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApproveTripNameCommand))]
    private string _proposedTripName = "";
    private TaskCompletionSource<string>? _tripNameApproval;
    private CancellationTokenSource? _runCts;

    private Task<string> AskTripNameAsync(string suggested)
    {
        ProposedTripName = suggested;
        _tripNameApproval = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        IsNamePromptOpen = true;
        return _tripNameApproval.Task;
    }

    private bool CanApproveTripName() => !string.IsNullOrWhiteSpace(ProposedTripName);

    [RelayCommand(CanExecute = nameof(CanApproveTripName))]
    private void ApproveTripName()
    {
        IsNamePromptOpen = false;
        _tripNameApproval?.TrySetResult(ProposedTripName.Trim());
    }

    /// <summary>Aborts the whole run; GenerateAsync then resets the Make Map page.</summary>
    [RelayCommand]
    private void CancelTrip()
    {
        IsNamePromptOpen = false;
        _runCts?.Cancel();
        _tripNameApproval?.TrySetCanceled();
    }

    /// <summary>"Make another map" on the success screen.</summary>
    [RelayCommand]
    private void StartOver() => ResetMakeMapPage();

    private void ResetMakeMapPage()
    {
        _inputContent = null;
        InputFileName = "";
        HasResult = false;
        ErrorText = "";
        StatusText = "";
        Progress = 0;
        GeocodeWarning = "";
        ResultFiles.Clear();
    }

    public MainViewModel(IPlatformServices platform)
    {
        _platform = platform;
        var settings = platform.LoadSettings();
        _googleApiKey = settings.GoogleApiKey;
        _geoApiKey = settings.GeoApiKey;
        _gcpSaJson = settings.GcpSaJson;
        _analyticsSheetId = settings.AnalyticsSheetId;
        _hasPublishSession = _platform.LoadSession() is not null;
        _isHebrew = settings.Language == "he";
        Loc.SetHebrew(_isHebrew);
        RefreshSetupStatus();
    }

    /// <summary>Stores a session.json (from the login helper) for cloud publishing. Browser host only.</summary>
    public void LoadSessionContent(string json)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("storageState", out _))
            {
                SessionStatus = Loc.T("SessionNoStorageState");
                return;
            }
            _platform.SaveSession(json);
            HasPublishSession = true;
            SessionStatus = Loc.T("SessionReady");
        }
        catch
        {
            SessionStatus = Loc.T("SessionBadJson");
        }
    }

    partial void OnGoogleApiKeyChanged(string value) => SaveSettings();
    partial void OnGeoApiKeyChanged(string value) => SaveSettings();
    partial void OnGcpSaJsonChanged(string value) => SaveSettings();
    partial void OnAnalyticsSheetIdChanged(string value) => SaveSettings();
    partial void OnIsBusyChanged(bool value) => GenerateCommand.NotifyCanExecuteChanged();
    partial void OnInputFileNameChanged(string value) => GenerateCommand.NotifyCanExecuteChanged();

    private AppSettings CurrentSettings() => new()
    {
        GoogleApiKey = GoogleApiKey,
        GeoApiKey = GeoApiKey,
        GcpSaJson = GcpSaJson,
        AnalyticsSheetId = AnalyticsSheetId,
        Language = IsHebrew ? "he" : "en",
    };

    private void SaveSettings()
    {
        _platform.SaveSettings(CurrentSettings());
        RefreshSetupStatus();
    }

    /// <summary>Recomputes the green/⚪ checklist from the saved keys and the host's login/Drive state.</summary>
    private void RefreshSetupStatus()
    {
        HasGeminiKey = !string.IsNullOrWhiteSpace(GoogleApiKey);
        HasGeoKey = !string.IsNullOrWhiteSpace(GeoApiKey);
        var status = _platform.GetSetupStatus();
        HasGoogleLogin = status.HasGoogleLogin;
        HasDriveCredentials = status.HasDriveCredentials;
        HasDriveToken = status.HasDriveToken;
    }

    /// <summary>
    /// Applies a one-file setup JSON: fills the key fields and, if it carries a
    /// `credentials` object and the host can publish, stores credentials.json.
    /// </summary>
    public async Task LoadSetupBundleAsync(IStorageFile file)
    {
        try
        {
            var merged = SetupBundleService.MergeFromText(await ReadTextAsync(file), CurrentSettings());
            var applied = merged.Applied;
            if (merged.CredentialsJson is not null)
            {
                if (Features.Publish)
                {
                    try { _platform.SaveDriveCredentials(merged.CredentialsJson); }
                    catch { applied.Remove("credentials.json"); } // not written, so not applied — keys still load
                }
                else applied.Remove("credentials.json");
            }
            if (!Features.Analytics)
            {
                // No usage ring / Analytics Sheet on this host — don't persist their secrets
                // (e.g. the browser would write the SA private key to localStorage).
                applied.Remove("GCP_SA_JSON");
                applied.Remove("ANALYTICS_SHEET_ID");
            }
            if (applied.Count == 0)
            {
                SetupMessage = Loc.T("SetupNothing");
                return;
            }

            GoogleApiKey = merged.Settings.GoogleApiKey;
            GeoApiKey = merged.Settings.GeoApiKey;
            if (Features.Analytics)
            {
                GcpSaJson = merged.Settings.GcpSaJson;
                AnalyticsSheetId = merged.Settings.AnalyticsSheetId;
            }
            SaveSettings();
            SetupMessage = Loc.F("SetupLoaded", string.Join(", ", applied));
            _ = RefreshUsageAsync();
        }
        catch (Exception e)
        {
            SetupMessage = e.Message;
        }
    }

    /// <summary>Stores a chosen Drive OAuth client as credentials.json.</summary>
    public async Task LoadDriveCredentialsAsync(IStorageFile file)
    {
        try
        {
            var text = await ReadTextAsync(file);
            JsonNode.Parse(text); // reject a non-JSON file before overwriting
            _platform.SaveDriveCredentials(text);
            RefreshSetupStatus();
            SetupMessage = Loc.T("CredsSaved");
        }
        catch (Exception e)
        {
            SetupMessage = Loc.F("CredsInvalid", e.Message);
        }
    }

    /// <summary>Loads a session.json file (login helper output) for cloud publishing.</summary>
    public async Task LoadSessionFileAsync(IStorageFile file) => LoadSessionContent(await ReadTextAsync(file));

    /// <summary>
    /// Loads the live usage gauge (best-effort). Hidden when the host has no analytics, no
    /// service account is configured, or Monitoring can't be read — never surfaces an error.
    /// Runs on the UI thread (called from the view / after a geocoded run) so binding updates are safe.
    /// </summary>
    public async Task RefreshUsageAsync()
    {
        if (_usageLoading) return;
        if (!Features.Analytics || string.IsNullOrWhiteSpace(GcpSaJson)) { HasUsage = false; return; }

        _usageLoading = true;
        try
        {
            var gauge = await _platform.GetUsageAsync(GcpSaJson);
            if (gauge is null) { HasUsage = false; return; }

            UsageRingGeometry = Geometry.Parse(UsageRing.ArcGeometry(gauge.Percent));
            UsageColor = UsageRing.GaugeColor(gauge.Percent);
            UsagePercentText = $"{gauge.Percent:0}%";
            _lastGauge = gauge;
            FormatUsageText();
            HasUsage = true;
        }
        finally
        {
            _usageLoading = false;
        }
    }

    private void FormatUsageText()
    {
        if (_lastGauge is not { } gauge) return;
        var reset = gauge.ResetDays switch
        {
            null => "",
            1 => "\n" + Loc.T("ResetsTomorrow"),
            var d => "\n" + Loc.F("ResetsInDays", d),
        };
        UsageSubText = Loc.F("UsageSub", gauge.Used.ToString("N0"), gauge.Limit.ToString("N0")) + reset;
    }

    /// <summary>Accepts a dropped or picked itinerary, rejecting the wrong type or an oversized file.</summary>
    public async Task LoadInputFileAsync(IStorageFile file)
    {
        var ext = Path.GetExtension(file.Name).ToLowerInvariant();
        if (ext is not (".pdf" or ".txt"))
        {
            ErrorText = Loc.T("ErrOnlyPdfTxt");
            return;
        }
        // Check the size before reading, so an accidental huge file is never pulled into memory.
        var size = (await file.GetBasicPropertiesAsync()).Size;
        var maxMb = Features.MaxUploadMb;
        if (size is { } bytes && bytes / 1e6 > maxMb)
        {
            ErrorText = Loc.F("ErrTooLarge", (bytes / 1e6).ToString("F1"), maxMb);
            return;
        }

        var content = await ReadBytesAsync(file);
        if (content.Length / 1e6 > maxMb)
        {
            ErrorText = Loc.F("ErrTooLarge", (content.Length / 1e6).ToString("F1"), maxMb);
            return;
        }
        ErrorText = "";
        _inputContent = content;
        InputFileName = file.Name;
    }

    [RelayCommand]
    private void ShowMakeMap() => Page = AppPage.MakeMap;

    [RelayCommand]
    private void ShowAnalytics() => Page = AppPage.Analytics;

    [RelayCommand]
    private void ShowSettings() => Page = AppPage.Settings;

    private bool CanGenerate() => !IsBusy && _inputContent is not null;

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateAsync()
    {
        if (string.IsNullOrWhiteSpace(GoogleApiKey))
        {
            // Stay put: the error banner lives on this page, so jumping to Settings
            // would drop the user there with no explanation.
            ErrorText = Loc.T("ErrNoGeminiKey");
            return;
        }
        if (_inputContent is null) return;

        IsBusy = true;
        HasResult = false;
        ErrorText = "";
        Progress = 0;
        ResultFiles.Clear();
        _runCts = new CancellationTokenSource();
        try
        {
            var gemini = new GeminiExtractionService(Http, GoogleApiKey, inlineFiles: Features.InlineFiles);
            var geocoding = new GeocodingService(Http, GeoApiKey);
            var pipeline = new PipelineService(gemini, geocoding);

            var result = await pipeline.GenerateAsync(
                InputFileName,
                _inputContent,
                layersPerFile: LayersPerFile,
                noGeocode: SkipGeocoding,
                progress: (step, frac) =>
                {
                    StatusText = Loc.Progress(step);
                    Progress = frac;
                },
                confirmTripName: AskTripNameAsync,
                ct: _runCts.Token);

            ResultTripName = result.TripName;
            ResultDays = result.Days.ToString();
            ResultLocations = result.Locations.ToString();
            ResultExactCoords = $"{result.Corrected}/{result.Corrected + result.Fallback}";
            GeocodeWarning = result.GeocodeWarning ?? "";
            foreach (var kml in result.KmlFiles) ResultFiles.Add(KmlFileItem.From(kml));
            HasResult = true;
            StatusText = "";

            if (PublishEnabled && Features.Publish && (!Features.RequiresSession || HasPublishSession))
                await PublishAsync(result.TripName, result.KmlFiles);
            else if (PublishEnabled && Features.Publish && Features.RequiresSession && !HasPublishSession)
                ErrorText = Loc.T("ErrNoSession");

            // Log the run to the analytics Google Sheet (best-effort; no-op if unconfigured).
            // Fire-and-forget: logging never throws, and a slow/unreachable Sheet must not keep
            // the finished run "busy". Materialize off the UI collection before firing so the
            // deferred continuation never touches ResultFiles off the UI thread.
            if (Features.Analytics)
            {
                var mapCount = ResultFiles.Count(f => f.HasMap);
                var mapLinks = ResultFiles.Where(f => f.HasMap).Select(f => f.MapUrl).ToList();
                _ = _platform.RecordTripAsync(
                    GcpSaJson, AnalyticsSheetId, result.TripName, mapCount, result.Locations, mapLinks);
            }
        }
        catch (OperationCanceledException) when (_runCts.IsCancellationRequested)
        {
            ResetMakeMapPage();
            return; // finally still runs; nothing was geocoded worth a gauge refresh
        }
        catch (Exception e)
        {
            ErrorText = e.Message;
            StatusText = "";
        }
        finally
        {
            _runCts.Dispose();
            _runCts = null;
            // A failed extraction can leave the name prompt open with nothing waiting on it.
            IsNamePromptOpen = false;
            _tripNameApproval = null;
            IsBusy = false;
        }

        // A geocoded run just spent Places quota — refresh the gauge once (the only
        // refresh besides app launch), matching the Python app. Skipped when geocoding was off.
        if (!SkipGeocoding) await RefreshUsageAsync();
    }

    /// <summary>
    /// Creates one My Maps map per KML file and shares it. Publishing failing must never
    /// discard the KML files already generated, so this reports into the file rows and the
    /// error banner rather than throwing out of the run.
    /// </summary>
    private async Task PublishAsync(string tripName, IReadOnlyList<KmlFile> files)
    {
        try
        {
            var maps = await _platform.PublishAsync(
                tripName,
                files,
                ShareEmailsList.ToList(),
                SelectedRole,
                NotifyShare,
                ShowBrowser,
                (step, frac) =>
                {
                    StatusText = Loc.Progress(step);
                    Progress = frac;
                });

            var byFile = ResultFiles.ToDictionary(f => f.FileName, StringComparer.OrdinalIgnoreCase);
            foreach (var map in maps)
            {
                if (!byFile.TryGetValue(map.FileName, out var row)) continue;
                row.MapError = map.Error;
                if (map.Error.Length == 0)
                {
                    row.MapUrl = map.ViewUrl;
                    row.SharedWith = map.SharedWith.Count > 0
                        ? Loc.F("SharedWith", string.Join(", ", map.SharedWith))
                        : Loc.T("NotShared");
                }
            }

            var ok = maps.Count(m => m.Error.Length == 0);
            StatusText = "";
            if (ok < maps.Count)
                ErrorText = Loc.F("PublishedPartial", ok, maps.Count);
        }
        catch (Exception e)
        {
            // Auth/setup failure before the per-file loop: the KML files still exist.
            StatusText = "";
            ErrorText = Loc.F("ErrPublish", e.Message);
        }
    }

    [RelayCommand]
    private async Task LogInToGoogleAsync()
    {
        IsLoggingIn = true;
        LoginStatus = Loc.T("LoginOpening");
        try
        {
            await _platform.LoginAsync();
            LoginStatus = Loc.T("LoginOk");
        }
        catch (Exception e)
        {
            LoginStatus = Loc.F("LoginFailed", e.Message);
        }
        finally
        {
            IsLoggingIn = false;
        }
    }

    [RelayCommand]
    private async Task CheckLoginAsync()
    {
        IsLoggingIn = true;
        LoginStatus = Loc.T("LoginChecking");
        try
        {
            LoginStatus = await _platform.IsLoggedInAsync()
                ? Loc.T("LoginSignedIn")
                : Loc.T("LoginNotSignedIn");
        }
        catch (Exception e)
        {
            LoginStatus = Loc.F("LoginCheckFailed", e.Message);
        }
        finally
        {
            IsLoggingIn = false;
        }
    }

    [RelayCommand]
    private void OpenMap(KmlFileItem? item)
    {
        if (item is { HasMap: true }) _platform.OpenUrl(item.MapUrl);
    }

    // --- Update commands ----------------------------------------------------
    private bool NotChecking() => !IsCheckingUpdate;

    [RelayCommand(CanExecute = nameof(NotChecking))]
    private async Task CheckForUpdatesAsync()
    {
        IsCheckingUpdate = true;
        UpdateAvailable = false;
        UpdateStatus = Loc.T("UpdChecking");
        _pendingUpdate = null;
        try
        {
            var info = await _platform.CheckForUpdateAsync();
            if (info is null) { UpdateStatus = Loc.T("UpdNoConnection"); return; }
            if (!info.HasUpdate) { UpdateStatus = Loc.F("UpdLatest", info.Current); return; }

            _pendingUpdate = info;
            if (info.HasAsset && UpdateService.IsSelfUpdateSupported)
            {
                UpdateAvailable = true;
                UpdateStatus = Loc.F("UpdAvailable", info.Latest);
            }
            else
            {
                // Reachable release but no installer for this OS — point at the page instead.
                UpdateStatus = Loc.F("UpdAvailableManual", info.Latest);
            }
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    private bool CanInstallUpdate() => !IsCheckingUpdate && _pendingUpdate is { HasAsset: true };

    [RelayCommand(CanExecute = nameof(CanInstallUpdate))]
    private async Task DownloadAndInstallUpdateAsync()
    {
        if (_pendingUpdate is not { HasAsset: true } info) return;
        IsCheckingUpdate = true;
        try
        {
            UpdateStatus = Loc.T("UpdDownloading");
            await _platform.InstallUpdateAsync(info, p => UpdateStatus = Loc.F("UpdDownloadingPct", p.ToString("P0")));
            UpdateStatus = Loc.T("UpdInstalling");
        }
        catch (Exception e)
        {
            UpdateStatus = Loc.F("UpdFailed", e.Message);
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    [RelayCommand]
    private void OpenReleasePage()
    {
        var url = _pendingUpdate is { HtmlUrl.Length: > 0 } info
            ? info.HtmlUrl
            : $"https://github.com/{AppConfig.GithubRepo}/releases";
        _platform.OpenUrl(url);
    }

    /// <summary>Hands the generated KML to the user (desktop: a chosen folder; browser: downloads).</summary>
    public async Task SaveKmlFilesAsync(IStorageProvider storage)
    {
        if (ResultFiles.Count == 0) return;
        try
        {
            var status = await _platform.SaveKmlFilesAsync(storage, ResultFiles.Select(f => f.File).ToList());
            if (status.Length > 0) StatusText = status;
        }
        catch (Exception e)
        {
            ErrorText = Loc.F("ErrSaveKml", e.Message);
        }
    }

    private static async Task<byte[]> ReadBytesAsync(IStorageFile file)
    {
        await using var stream = await file.OpenReadAsync();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static async Task<string> ReadTextAsync(IStorageFile file)
    {
        await using var stream = await file.OpenReadAsync();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}
