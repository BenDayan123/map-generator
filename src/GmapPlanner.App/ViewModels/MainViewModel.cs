using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json.Nodes;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GmapPlanner.Core;
using GmapPlanner.Core.Services;
using GmapPlanner.Core.Services.Gemini;
using GmapPlanner.Core.Services.Publish;

namespace GmapPlanner.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private static readonly HttpClient Http = new();

    // Matches the Streamlit app's upload cap.
    private const int MaxUploadMb = 15;

    public int MaxLayersPerFile => AppConfig.MaxLayersPerFile;

    // --- Navigation ---------------------------------------------------------
    [ObservableProperty] private bool _isSettingsPage;
    public bool IsMakeMapPage => !IsSettingsPage;
    partial void OnIsSettingsPageChanged(bool value) => OnPropertyChanged(nameof(IsMakeMapPage));

    // --- Input + settings ---------------------------------------------------
    [ObservableProperty] private string _inputFilePath = "";
    [ObservableProperty] private string _inputFileName = "";
    [ObservableProperty] private string _googleApiKey;
    [ObservableProperty] private string _geoApiKey;
    [ObservableProperty] private string _outputDir;
    [ObservableProperty] private string _gcpSaJson;
    [ObservableProperty] private string _setupMessage = "";

    // --- Setup status (green/⚪ checklist) -----------------------------------
    [ObservableProperty] private bool _hasGeminiKey;
    [ObservableProperty] private bool _hasGeoKey;
    [ObservableProperty] private bool _hasGoogleLogin;
    [ObservableProperty] private bool _hasDriveCredentials;
    [ObservableProperty] private bool _hasDriveToken;

    // --- Geocoding usage gauge ----------------------------------------------
    private readonly UsageService _usage = new(Http);
    private bool _usageLoading;
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
    [ObservableProperty] private string _shareEmails = "";
    [ObservableProperty] private int _shareRoleIndex; // 0 viewer, 1 commenter, 2 editor
    [ObservableProperty] private bool _notifyShare = true;
    [ObservableProperty] private bool _showBrowser;
    [ObservableProperty] private string _loginStatus = "";
    [ObservableProperty] private bool _isLoggingIn;

    public string[] ShareRoles { get; } = ["viewer", "commenter", "editor"];

    private string SelectedRole =>
        ShareRoles[Math.Clamp(ShareRoleIndex, 0, ShareRoles.Length - 1)];

    /// <summary>Recipients, de-duplicated, from the comma/newline separated box.</summary>
    private List<string> Recipients => ShareEmails
        .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

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
    [ObservableProperty] private string _resultOutputDir = "";

    public ObservableCollection<KmlFileItem> ResultFiles { get; } = [];

    public MainViewModel()
    {
        var settings = AppSettingsService.Load();
        _googleApiKey = settings.GoogleApiKey;
        _geoApiKey = settings.GeoApiKey;
        _gcpSaJson = settings.GcpSaJson;
        _outputDir = string.IsNullOrEmpty(settings.OutputDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
            : settings.OutputDir;
        RefreshSetupStatus();
    }

    partial void OnGoogleApiKeyChanged(string value) => SaveSettings();
    partial void OnGeoApiKeyChanged(string value) => SaveSettings();
    partial void OnOutputDirChanged(string value) => SaveSettings();
    partial void OnGcpSaJsonChanged(string value) => SaveSettings();
    partial void OnIsBusyChanged(bool value) => GenerateCommand.NotifyCanExecuteChanged();

    partial void OnInputFilePathChanged(string value)
    {
        InputFileName = string.IsNullOrEmpty(value) ? "" : Path.GetFileName(value);
        GenerateCommand.NotifyCanExecuteChanged();
    }

    private void SaveSettings()
    {
        AppSettingsService.Save(new AppSettings
        {
            GoogleApiKey = GoogleApiKey,
            GeoApiKey = GeoApiKey,
            OutputDir = OutputDir,
            GcpSaJson = GcpSaJson,
        });
        RefreshSetupStatus();
    }

    /// <summary>Recomputes the green/⚪ checklist from the saved keys, profile, and files.</summary>
    private void RefreshSetupStatus()
    {
        HasGeminiKey = !string.IsNullOrWhiteSpace(GoogleApiKey);
        HasGeoKey = !string.IsNullOrWhiteSpace(GeoApiKey);
        HasGoogleLogin = DirHasFiles(AppConfig.PlaywrightProfileDir);
        HasDriveCredentials = File.Exists(AppConfig.DriveCredentialsFile);
        HasDriveToken = DirHasFiles(AppConfig.DriveTokenDir);
    }

    private static bool DirHasFiles(string path)
    {
        try { return Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any(); }
        catch { return false; }
    }

    /// <summary>
    /// Applies a one-file setup JSON: fills the key fields and, if it carries a
    /// `credentials` object, writes credentials.json. Called by the file picker.
    /// </summary>
    public void ApplySetupBundleFile(string path)
    {
        try
        {
            var result = SetupBundleService.ApplyFromText(File.ReadAllText(path));
            if (!result.AnythingApplied)
            {
                SetupMessage = "Nothing loaded — no recognized keys in that file.";
                return;
            }
            // Reload so the fields (and status) reflect what the bundle wrote.
            var settings = AppSettingsService.Load();
            GoogleApiKey = settings.GoogleApiKey;
            GeoApiKey = settings.GeoApiKey;
            GcpSaJson = settings.GcpSaJson;
            RefreshSetupStatus();
            SetupMessage = "Loaded: " + string.Join(", ", result.Applied) + ".";
            _ = RefreshUsageAsync();
        }
        catch (Exception e)
        {
            SetupMessage = e.Message;
        }
    }

    /// <summary>Copies a chosen Drive OAuth client into place as credentials.json.</summary>
    public void SetDriveCredentialsFile(string path)
    {
        try
        {
            var text = File.ReadAllText(path);
            JsonNode.Parse(text); // reject a non-JSON file before overwriting
            File.WriteAllText(AppConfig.DriveCredentialsFile, text);
            RefreshSetupStatus();
            SetupMessage = "Saved credentials.json.";
        }
        catch (Exception e)
        {
            SetupMessage = $"Not a valid credentials.json: {e.Message}";
        }
    }

    /// <summary>
    /// Loads the live geocoding-usage gauge (best-effort). Hidden when no service account
    /// is configured or Monitoring can't be read — never surfaces an error. Runs on the
    /// UI thread (called from the view / after a geocoded run) so binding updates are safe.
    /// </summary>
    public async Task RefreshUsageAsync()
    {
        if (_usageLoading) return;
        if (string.IsNullOrWhiteSpace(GcpSaJson)) { HasUsage = false; return; }

        _usageLoading = true;
        try
        {
            var gauge = await _usage.GetGeocodeUsageAsync(GcpSaJson);
            if (gauge is null) { HasUsage = false; return; }

            UsageRingGeometry = Geometry.Parse(UsageRing.ArcGeometry(gauge.Percent));
            UsageColor = UsageRing.GaugeColor(gauge.Percent);
            UsagePercentText = $"{gauge.Percent:0}%";
            var reset = gauge.ResetDays switch
            {
                null => "",
                1 => "\nresets tomorrow",
                var d => $"\nresets in {d} days",
            };
            UsageSubText = $"Geocoding · this month\n{gauge.Used:N0} / {gauge.Limit:N0}{reset}";
            HasUsage = true;
        }
        finally
        {
            _usageLoading = false;
        }
    }

    /// <summary>Accepts a dropped or picked itinerary, rejecting the wrong type or an oversized file.</summary>
    public void SetInputFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not (".pdf" or ".txt"))
        {
            ErrorText = "Only PDF or TXT itineraries are supported.";
            return;
        }
        var sizeMb = new FileInfo(path).Length / 1e6;
        if (sizeMb > MaxUploadMb)
        {
            ErrorText = $"File is too large ({sizeMb:F1} MB). Max is {MaxUploadMb} MB.";
            return;
        }
        ErrorText = "";
        InputFilePath = path;
    }

    [RelayCommand]
    private void ShowMakeMap() => IsSettingsPage = false;

    [RelayCommand]
    private void ShowSettings() => IsSettingsPage = true;

    private bool CanGenerate() => !IsBusy && !string.IsNullOrWhiteSpace(InputFilePath);

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateAsync()
    {
        if (string.IsNullOrWhiteSpace(GoogleApiKey))
        {
            // Stay put: the error banner lives on this page, so jumping to Settings
            // would drop the user there with no explanation.
            ErrorText = "No Gemini API key configured — add one on the ⚙️ Settings page.";
            return;
        }

        IsBusy = true;
        HasResult = false;
        ErrorText = "";
        Progress = 0;
        ResultFiles.Clear();
        try
        {
            var gemini = new GeminiExtractionService(Http, GoogleApiKey);
            var geocoding = new GeocodingService(Http, GeoApiKey);
            var pipeline = new PipelineService(gemini, geocoding);

            var result = await pipeline.RunAsync(
                InputFilePath,
                OutputDir,
                layersPerFile: LayersPerFile,
                noGeocode: SkipGeocoding,
                progress: (step, frac) =>
                {
                    StatusText = step;
                    Progress = frac;
                });

            ResultTripName = result.TripName;
            ResultDays = result.Days.ToString();
            ResultLocations = result.Locations.ToString();
            ResultExactCoords = $"{result.Corrected}/{result.Corrected + result.Fallback}";
            ResultOutputDir = result.OutputDir;
            foreach (var path in result.Files) ResultFiles.Add(KmlFileItem.FromPath(path));
            HasResult = true;
            StatusText = "";

            if (PublishEnabled) await PublishAsync(result.Files, result.TripName);
        }
        catch (Exception e)
        {
            ErrorText = e.Message;
            StatusText = "";
        }
        finally
        {
            IsBusy = false;
        }

        // A geocoded run just spent Geocoding quota — refresh the gauge once (the only
        // refresh besides app launch), matching the Python app. Skipped when geocoding was off.
        if (!SkipGeocoding) await RefreshUsageAsync();
    }

    /// <summary>
    /// Creates one My Maps map per KML file and shares it. Publishing failing must never
    /// discard the KML files that were already written, so this reports into the file
    /// rows and the error banner rather than throwing out of the run.
    /// </summary>
    private async Task PublishAsync(IReadOnlyList<string> files, string tripName)
    {
        var recipients = Recipients;
        try
        {
            var maps = await PublishService.PublishKmlFilesAsync(
                files,
                tripName,
                recipients,
                role: SelectedRole,
                headless: !ShowBrowser,
                notify: NotifyShare,
                progress: (step, frac) =>
                {
                    StatusText = step;
                    Progress = frac;
                });

            var byFile = ResultFiles.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
            foreach (var map in maps)
            {
                if (!byFile.TryGetValue(map.File, out var row)) continue;
                row.MapError = map.Error;
                if (map.Error.Length == 0)
                {
                    row.MapUrl = map.ViewUrl;
                    row.SharedWith = map.SharedWith.Count > 0
                        ? $"Shared with: {string.Join(", ", map.SharedWith)}"
                        : "Not shared";
                }
            }

            var ok = maps.Count(m => m.Error.Length == 0);
            StatusText = "";
            if (ok < maps.Count)
                ErrorText = $"Published {ok}/{maps.Count} map(s) — see the per-file notes below.";
        }
        catch (Exception e)
        {
            // Auth/setup failure before the per-file loop: the KML files still exist.
            StatusText = "";
            ErrorText = $"Maps couldn't be published (the KML files were still created): {e.Message}";
        }
    }

    [RelayCommand]
    private async Task LogInToGoogleAsync()
    {
        IsLoggingIn = true;
        LoginStatus = "Opening a browser window — sign in to Google, then return here…";
        try
        {
            await MyMapsSession.LoginAsync();
            LoginStatus = "✅ Signed in to Google. The session is saved for future runs.";
        }
        catch (Exception e)
        {
            LoginStatus = $"⚠️ Login failed: {e.Message}";
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
        LoginStatus = "Checking the saved Google session…";
        try
        {
            await using var session = await MyMapsSession.StartAsync(headless: true);
            LoginStatus = await session.IsLoggedInAsync()
                ? "✅ Signed in to Google."
                : "⚠️ Not signed in — click 'Log in to Google'.";
        }
        catch (Exception e)
        {
            LoginStatus = $"⚠️ Could not check the session: {e.Message}";
        }
        finally
        {
            IsLoggingIn = false;
        }
    }

    [RelayCommand]
    private void OpenMap(KmlFileItem? item)
    {
        if (item is null || !item.HasMap) return;
        try
        {
            Process.Start(new ProcessStartInfo(item.MapUrl) { UseShellExecute = true });
        }
        catch
        {
            // Opening a browser is a nicety; never fail the run over it.
        }
    }

    [RelayCommand]
    private void OpenOutputFolder()
    {
        if (string.IsNullOrEmpty(ResultOutputDir) || !Directory.Exists(ResultOutputDir)) return;
        try
        {
            // Best-effort reveal, like the Python app's reveal_in_file_manager.
            var (exe, args) = OperatingSystem.IsMacOS()
                ? ("open", ResultOutputDir)
                : ("explorer.exe", ResultOutputDir);
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
        }
        catch
        {
            // Opening a file manager is a nicety; never fail the run over it.
        }
    }
}
