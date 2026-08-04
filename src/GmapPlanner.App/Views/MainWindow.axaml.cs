using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using GmapPlanner.App.ViewModels;

namespace GmapPlanner.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        BrowseInputButton.Click += async (_, _) => await SafeAsync(BrowseInputFileAsync);
        BrowseOutputButton.Click += async (_, _) => await SafeAsync(BrowseOutputFolderAsync);

        DragDrop.SetAllowDrop(DropZone, true);
        DropZone.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        DropZone.AddHandler(DragDrop.DropEvent, OnDrop);
    }

    /// <summary>
    /// Click handlers are async void, so anything thrown inside one takes the whole
    /// process down instead of surfacing. Show it on the page instead.
    /// </summary>
    private async Task SafeAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception e) when (DataContext is MainViewModel vm)
        {
            vm.ErrorText = e.Message;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var path = e.Data.GetFiles()?.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null) vm.SetInputFile(path);
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
        if (path is not null) vm.SetInputFile(path);
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
