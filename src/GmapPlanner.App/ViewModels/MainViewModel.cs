using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GmapPlanner.Core.Services;
using GmapPlanner.Core.Services.Gemini;

namespace GmapPlanner.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private static readonly HttpClient Http = new();

    [ObservableProperty] private string _inputFilePath = "";
    [ObservableProperty] private string _googleApiKey;
    [ObservableProperty] private string _geoApiKey;
    [ObservableProperty] private string _outputDir;
    [ObservableProperty] private string _statusText = "Pick an itinerary (.pdf or .txt) to begin.";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;

    public MainViewModel()
    {
        var settings = AppSettingsService.Load();
        _googleApiKey = settings.GoogleApiKey;
        _geoApiKey = settings.GeoApiKey;
        _outputDir = string.IsNullOrEmpty(settings.OutputDir)
            ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
            : settings.OutputDir;
    }

    partial void OnGoogleApiKeyChanged(string value) => SaveSettings();
    partial void OnGeoApiKeyChanged(string value) => SaveSettings();
    partial void OnOutputDirChanged(string value) => SaveSettings();
    partial void OnInputFilePathChanged(string value) => RunCommand.NotifyCanExecuteChanged();
    partial void OnIsBusyChanged(bool value) => RunCommand.NotifyCanExecuteChanged();

    private void SaveSettings() => AppSettingsService.Save(new AppSettings
    {
        GoogleApiKey = GoogleApiKey,
        GeoApiKey = GeoApiKey,
        OutputDir = OutputDir,
    });

    private bool CanRun() => !IsBusy && !string.IsNullOrWhiteSpace(InputFilePath);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (string.IsNullOrWhiteSpace(GoogleApiKey))
        {
            StatusText = "No Gemini API key provided.";
            return;
        }

        IsBusy = true;
        Progress = 0;
        try
        {
            var gemini = new GeminiExtractionService(Http, GoogleApiKey);
            var geocoding = new GeocodingService(Http, GeoApiKey);
            var pipeline = new PipelineService(gemini, geocoding);

            var result = await pipeline.RunAsync(
                InputFilePath,
                OutputDir,
                progress: (step, frac) =>
                {
                    StatusText = step;
                    Progress = frac;
                });

            StatusText =
                $"Done: \"{result.TripName}\" — {result.Days} day(s), {result.Locations} location(s) " +
                $"({result.Corrected} geocoded, {result.Fallback} from Gemini). " +
                $"{result.Files.Count} KML file(s) written to {result.OutputDir}.";
        }
        catch (Exception e)
        {
            StatusText = $"Error: {e.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }
}
