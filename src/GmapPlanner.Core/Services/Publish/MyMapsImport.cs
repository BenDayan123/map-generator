namespace GmapPlanner.Core.Services.Publish;

/// <summary>
/// The browser operations the import retry needs. Exists so the retry loop — the
/// flakiest part of publishing — can be tested without driving a real browser, the same
/// way test_mymaps_helpers.py stubs out the Playwright calls.
/// </summary>
internal interface IImportSurface
{
    Task ClickImportAsync();

    /// <summary>Sets the KML on the Picker's file input. False if no input was found.</summary>
    Task<bool> SetFileAsync();

    Task<bool> IsPickerOpenAsync();
    Task<bool> IsRevertedAsync();

    /// <summary>
    /// Has the KML's content actually landed in the editor (first placemark rendered)?
    /// Some Picker variants import the file but never auto-close — they reset to the drag
    /// view and linger — so a closed dialog is not a reliable success signal on its own.
    /// </summary>
    Task<bool> IsImportedAsync();

    Task PressEscapeAsync();

    /// <summary>Does the URL already carry a mid (i.e. the map exists and was saved)?</summary>
    bool HasMid { get; }

    Task ReloadAsync();
    Task DelayAsync(int milliseconds);
}

/// <summary>
/// The click → set-file → dialog-closes cycle, with the retries that make importing
/// survive My Maps. Ported from mymaps.py's _do_import / _dismiss_picker /
/// _wait_for_picker_close / _recover_from_revert.
/// </summary>
internal static class MyMapsImport
{
    /// <summary>
    /// Escapes out of a leftover Picker dialog. Anything clicked while it's open
    /// (notably 'Import' on a retry) lands on the modal's backdrop and does nothing —
    /// which is what leaves a run stuck staring at the drag-and-drop dialog.
    /// </summary>
    public static async Task DismissPickerAsync(IImportSurface surface, Action<string>? log = null)
    {
        for (var i = 0; i < 3; i++)
        {
            if (!await surface.IsPickerOpenAsync()) return;
            log?.Invoke("dismissing a Picker dialog that is still open");
            await surface.PressEscapeAsync();
            await surface.DelayAsync(700);
        }
    }

    /// <summary>
    /// Waits for the import to take, after the file was set. Succeeds when the dialog
    /// closes OR when the KML's content has rendered in the editor — some Picker variants
    /// import the file but never auto-close, resetting to the drag view and lingering, so
    /// waiting only for a closed dialog hangs on an import that already succeeded. Returns
    /// early when My Maps reverts the action: waiting out the full timeout there is what
    /// made a failed second map look like a long hang.
    /// </summary>
    public static async Task<bool> WaitForPickerCloseAsync(IImportSurface surface, int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (!await surface.IsPickerOpenAsync()) return true;
            if (await surface.IsImportedAsync()) return true;
            if (await surface.IsRevertedAsync()) return false;
            await surface.DelayAsync(200);
        }
        return false;
    }

    /// <summary>
    /// Reloads the editor after a reverted action so the retry starts clean. The toast
    /// means the editor's own state was rolled back; importing again into that stale page
    /// just gets reverted too. Only safe once the map exists — before that a reload would
    /// land on a fresh, empty editor.
    /// </summary>
    public static async Task RecoverFromRevertAsync(IImportSurface surface, Action<string>? log = null)
    {
        log?.Invoke("My Maps reverted the action — recovering");
        await surface.DelayAsync(4000);
        if (!surface.HasMid) return;
        await surface.ReloadAsync();
        await surface.DelayAsync(2500);
    }

    public static async Task RunAsync(
        IImportSurface surface,
        int attempts = 3,
        int closeTimeoutMs = 25000,
        Action<string>? log = null)
    {
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            await DismissPickerAsync(surface, log); // a stuck dialog swallows the 'Import' click
            log?.Invoke($"clicking 'Import' on the base layer (attempt {attempt}/{attempts})");
            await surface.ClickImportAsync();

            if (!await surface.SetFileAsync())
            {
                if (attempt == attempts)
                    throw new MyMapsException("Could not find the file-upload input in the import dialog.");
                continue;
            }

            if (await WaitForPickerCloseAsync(surface, closeTimeoutMs))
            {
                // The import took. A lingering Picker (the variant that doesn't auto-close)
                // would sit over the editor and block the rename that follows, so dismiss it.
                await DismissPickerAsync(surface, log);
                return;
            }

            if (await surface.IsRevertedAsync())
            {
                await DismissPickerAsync(surface, log);
                await RecoverFromRevertAsync(surface, log);
                continue;
            }
            log?.Invoke("the upload dialog is still open — the file did not take");
        }
        throw new MyMapsException("The My Maps import dialog stayed open after uploading the KML file.");
    }
}
