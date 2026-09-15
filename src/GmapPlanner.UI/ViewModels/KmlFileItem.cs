using CommunityToolkit.Mvvm.ComponentModel;

namespace GmapPlanner.App.ViewModels;

/// <summary>
/// One generated KML file in the results list. Publishing fills in the map fields
/// afterwards, so this is observable rather than a plain record.
/// </summary>
public partial class KmlFileItem : ObservableObject
{
    public required string DayLabel { get; init; }
    public required string FileName { get; init; }
    public required string SizeText { get; init; }
    public required string Path { get; init; }

    [ObservableProperty] private string _mapUrl = "";
    [ObservableProperty] private string _sharedWith = "";
    [ObservableProperty] private string _mapError = "";

    public bool HasMap => MapUrl.Length > 0;
    partial void OnMapUrlChanged(string value) => OnPropertyChanged(nameof(HasMap));

    public static KmlFileItem FromPath(string path)
    {
        var label = System.IO.Path.GetFileNameWithoutExtension(path);
        return new KmlFileItem
        {
            DayLabel = label.Contains('-') ? $"Days {label}" : $"Day {label}",
            FileName = System.IO.Path.GetFileName(path),
            SizeText = $"{new FileInfo(path).Length / 1024.0:F0} KB",
            Path = path,
        };
    }
}
