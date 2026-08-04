using Avalonia.Controls;
using Avalonia.Platform.Storage;
using GmapPlanner.App.ViewModels;

namespace GmapPlanner.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        BrowseInputButton.Click += async (_, _) => await BrowseInputFileAsync();
        BrowseOutputButton.Click += async (_, _) => await BrowseOutputFolderAsync();
    }

    private async Task BrowseInputFileAsync()
    {
        if (DataContext is not MainViewModel vm) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose an itinerary",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Itinerary (*.pdf, *.txt)") { Patterns = ["*.pdf", "*.txt"] },
            ],
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null) vm.InputFilePath = path;
    }

    private async Task BrowseOutputFolderAsync()
    {
        if (DataContext is not MainViewModel vm) return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Choose an output folder",
            AllowMultiple = false,
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null) vm.OutputDir = path;
    }
}
