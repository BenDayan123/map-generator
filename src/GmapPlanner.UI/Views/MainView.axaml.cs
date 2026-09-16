using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using GmapPlanner.App.ViewModels;

namespace GmapPlanner.App.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();

        BrowseInputButton.Click += async (_, _) => await SafeAsync(BrowseInputFileAsync);
        DownloadButton.Click += async (_, _) => await SafeAsync(() => Vm?.SaveKmlFilesAsync(Storage) ?? Task.CompletedTask);
        SetupBundleButton.Click += async (_, _) => await SafeAsync(BrowseSetupBundleAsync);
        CredentialsButton.Click += async (_, _) => await SafeAsync(BrowseCredentialsAsync);
        SessionButton.Click += async (_, _) => await SafeAsync(BrowseSessionAsync);

        // Email token input: Enter/Tab/separators commit a chip; Backspace on empty pops one;
        // losing focus commits whatever's half-typed so it isn't silently lost.
        EmailEntry.KeyDown += OnEmailEntryKeyDown;
        EmailEntry.LostFocus += (_, _) => CommitEmailEntry();

        // Drop a file onto the itinerary zone, or straight onto the Settings pickers.
        EnableFileDrop(DropZone, (vm, f) => vm.LoadInputFileAsync(f));
        EnableFileDrop(SetupBundleButton, (vm, f) => vm.LoadSetupBundleAsync(f));
        EnableFileDrop(CredentialsButton, (vm, f) => vm.LoadDriveCredentialsAsync(f));
        EnableFileDrop(SessionButton, (vm, f) => vm.LoadSessionFileAsync(f));

        // Hold the eye to reveal a masked API key; release (or leave) re-masks it.
        WireHoldReveal(GeminiKeyEye, GeminiKeyBox);
        WireHoldReveal(GeoKeyEye, GeoKeyBox);

        // Load the usage gauge once the view is up, on the UI thread so binding is safe.
        Loaded += async (_, _) =>
        {
            if (Vm is { } vm) await vm.RefreshUsageAsync();
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

    /// <summary>The hosting window's (or browser's) storage provider, for the file/folder pickers.</summary>
    private IStorageProvider Storage => TopLevel.GetTopLevel(this)!.StorageProvider;

    /// <summary>Cap the resizable sidebar at a third of the view; clamp if the view shrinks.</summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var sidebar = RootGrid.ColumnDefinitions[0];
        var max = Math.Max(sidebar.MinWidth, e.NewSize.Width / 3);
        sidebar.MaxWidth = max;
        if (sidebar.Width.IsAbsolute && sidebar.Width.Value > max)
            sidebar.Width = new Avalonia.Controls.GridLength(max);
    }

    private static FilePickerOpenOptions JsonPicker(string title) => new()
    {
        Title = title,
        AllowMultiple = false,
        FileTypeFilter = [new FilePickerFileType("JSON (*.json)") { Patterns = ["*.json"] }],
    };

    private async Task BrowseSetupBundleAsync()
    {
        var file = (await Storage.OpenFilePickerAsync(JsonPicker("Choose a setup file"))).FirstOrDefault();
        if (file is not null && Vm is { } vm) await vm.LoadSetupBundleAsync(file);
    }

    private async Task BrowseCredentialsAsync()
    {
        var file = (await Storage.OpenFilePickerAsync(JsonPicker("Choose the Drive credentials.json"))).FirstOrDefault();
        if (file is not null && Vm is { } vm) await vm.LoadDriveCredentialsAsync(file);
    }

    private async Task BrowseSessionAsync()
    {
        var file = (await Storage.OpenFilePickerAsync(JsonPicker("Choose session.json"))).FirstOrDefault();
        if (file is not null && Vm is { } vm) await vm.LoadSessionFileAsync(file);
    }

    private async Task BrowseInputFileAsync()
    {
        var file = (await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose an itinerary",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Itinerary (*.pdf, *.txt)") { Patterns = ["*.pdf", "*.txt"] },
            ],
        })).FirstOrDefault();
        if (file is not null && Vm is { } vm) await vm.LoadInputFileAsync(file);
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
        catch (Exception e) when (Vm is { } vm)
        {
            vm.ErrorText = e.Message;
        }
    }

    /// <summary>Lets a control accept a dropped file, handing the first file to the VM.</summary>
    private void EnableFileDrop(Control target, Func<MainViewModel, IStorageFile, Task> onFile)
    {
        DragDrop.SetAllowDrop(target, true);
        target.AddHandler(DragDrop.DragOverEvent, (_, e) =>
            e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None);
        target.AddHandler(DragDrop.DropEvent, async (_, e) => await SafeAsync(async () =>
        {
            if (e.Data.GetFiles()?.OfType<IStorageFile>().FirstOrDefault() is { } file && Vm is { } vm)
                await onFile(vm, file);
        }));
    }

    /// <summary>Reveals a password TextBox while the eye is held (tunnel, so the Button can't swallow it).</summary>
    private static void WireHoldReveal(Control eye, TextBox box)
    {
        eye.AddHandler(InputElement.PointerPressedEvent, (_, _) => box.RevealPassword = true, RoutingStrategies.Tunnel);
        eye.AddHandler(InputElement.PointerReleasedEvent, (_, _) => box.RevealPassword = false, RoutingStrategies.Tunnel);
        // Releasing off the button (drag away) still fires PointerExited — re-mask there too.
        eye.PointerExited += (_, _) => box.RevealPassword = false;
    }

    private void OnEmailEntryKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;

        // Backspace with nothing typed removes the last chip.
        if (e.Key == Key.Back && string.IsNullOrEmpty(tb.Text))
        {
            Vm?.RemoveLastEmail();
            return;
        }

        // Enter / Tab / , ; commit the typed text as chip(s).
        if (e.Key is Key.Enter or Key.Tab or Key.OemComma or Key.OemSemicolon)
        {
            if (CommitEmailEntry()) e.Handled = true;
        }
    }

    /// <summary>Turns whatever is in the entry box into chips and clears it. True if it had text.</summary>
    private bool CommitEmailEntry()
    {
        if (Vm is not { } vm || string.IsNullOrWhiteSpace(EmailEntry.Text)) return false;
        vm.AddEmails(EmailEntry.Text);
        EmailEntry.Text = "";
        return true;
    }
}
