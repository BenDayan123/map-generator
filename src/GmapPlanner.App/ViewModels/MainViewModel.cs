using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GmapPlanner.Core;
using GmapPlanner.Core.Services;
using GmapPlanner.Core.Services.Gemini;

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

    // --- Options (the Streamlit sidebar) ------------------------------------
    [ObservableProperty] private int _layersPerFile = AppConfig.MaxLayersPerFile;
    [ObservableProperty] private bool _skipGeocoding;

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
        _outputDir = string.IsNullOrEmpty(settings.OutputDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
            : settings.OutputDir;
    }

    partial void OnGoogleApiKeyChanged(string value) => SaveSettings();
    partial void OnGeoApiKeyChanged(string value) => SaveSettings();
    partial void OnOutputDirChanged(string value) => SaveSettings();
    partial void OnIsBusyChanged(bool value) => GenerateCommand.NotifyCanExecuteChanged();

    partial void OnInputFilePathChanged(string value)
    {
        InputFileName = string.IsNullOrEmpty(value) ? "" : Path.GetFileName(value);
        GenerateCommand.NotifyCanExecuteChanged();
    }

    private void SaveSettings() => AppSettingsService.Save(new AppSettings
    {
        GoogleApiKey = GoogleApiKey,
        GeoApiKey = GeoApiKey,
        OutputDir = OutputDir,
    });

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
