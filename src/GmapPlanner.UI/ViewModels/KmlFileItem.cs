using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using GmapPlanner.Core.Services;
using GmapPlanner.App.Localization;

namespace GmapPlanner.App.ViewModels;

/// <summary>
/// One generated KML file in the results list. Publishing fills in the map fields
/// afterwards, so this is observable rather than a plain record.
/// </summary>
public partial class KmlFileItem : ObservableObject
{
    public required KmlFile File { get; init; }
    public required string DayLabel { get; init; }
    public required string SizeText { get; init; }
    public string FileName => File.FileName;

    [ObservableProperty] private string _mapUrl = "";
    [ObservableProperty] private string _sharedWith = "";
    [ObservableProperty] private string _mapError = "";

    // Live My Maps upload state while PublishAsync runs; the end result is HasMap / MapError.
    [ObservableProperty] private bool _isWaiting;
    [ObservableProperty] private bool _isUploading;

    public bool HasMap => MapUrl.Length > 0;
    partial void OnMapUrlChanged(string value) => OnPropertyChanged(nameof(HasMap));

    public static KmlFileItem From(KmlFile file)
    {
        var label = Path.GetFileNameWithoutExtension(file.FileName);
        return new KmlFileItem
        {
            File = file,
            DayLabel = Loc.F(label.Contains('-') ? "DayLabelMany" : "DayLabelOne", label),
            SizeText = $"{Encoding.UTF8.GetByteCount(file.Content) / 1024.0:F0} KB",
        };
    }
}
