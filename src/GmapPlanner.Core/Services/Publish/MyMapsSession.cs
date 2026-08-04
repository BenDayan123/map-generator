using System.Text.RegularExpressions;
using System.Xml.Linq;
using GmapPlanner.Core.Errors;
using Microsoft.Playwright;

namespace GmapPlanner.Core.Services.Publish;

/// <summary>My Maps automation failed (login, UI change, import timeout, ...).</summary>
public class MyMapsException : PipelineException
{
    public MyMapsException(string message) : base(message) { }
    public MyMapsException(string message, Exception inner) : base(message, inner) { }
}

public record CreatedMap(string Title, string Url, string Mid);

/// <summary>
/// Ports gmap_planner/mymaps.py: drives the Google My Maps editor with Playwright to
/// create one map per KML file.
///
/// My Maps has no create/import API, so automating the editor UI is the only way. The
/// selectors are the fragile part — Google ships UI changes and localizes text, so the
/// editor is always opened with hl=en and selectors live in <see cref="MyMapsSelectors"/>.
/// On any failure a screenshot is written next to the KML.
///
/// First run needs a headed login (<see cref="LoginAsync"/>); the persistent Chromium
/// profile makes every later run headless.
/// </summary>
public sealed class MyMapsSession : IAsyncDisposable
{
    // Hide the two signals Google uses to block sign-in ("This browser or app may not
    // be secure"): the bundled-Chromium fingerprint and the --enable-automation flag.
    private static readonly string[] LaunchArgs = ["--disable-blink-features=AutomationControlled"];
    private static readonly string[] IgnoreArgs = ["--enable-automation"];

    private readonly string _profileDir;
    private readonly bool _headless;
    private readonly Action<string>? _log;
    private IPlaywright? _pw;
    private IBrowserContext? _ctx;
    private int _mapsCreated;

    private MyMapsSession(string profileDir, bool headless, Action<string>? log)
    {
        _profileDir = profileDir;
        _headless = headless;
        _log = log;
    }

    private void Log(string message) => _log?.Invoke($"[mymaps] {message}");

    public static async Task<MyMapsSession> StartAsync(
        string? profileDir = null, bool headless = true, Action<string>? log = null)
    {
        var session = new MyMapsSession(profileDir ?? AppConfig.PlaywrightProfileDir, headless, log);
        await session.OpenAsync();
        return session;
    }

    private async Task OpenAsync()
    {
        EnsureDriverInstalled();
        _pw = await Playwright.CreateAsync();
        _ctx = await LaunchPersistentAsync(_pw, _profileDir, _headless);
    }

    /// <summary>
    /// Installs Playwright's Chromium if it isn't there yet. Mirrors ensure_chromium():
    /// the browser is deliberately not bundled (that's what keeps the app small), so the
    /// first publish may download it.
    /// </summary>
    private static void EnsureDriverInstalled()
    {
        try
        {
            Microsoft.Playwright.Program.Main(["install", "chromium"]);
        }
        catch
        {
            // Best-effort — the launch step below reports a clear error if it's missing.
        }
    }

    /// <summary>
    /// Opens a persistent context as the *installed* Chrome with automation flags off.
    ///
    /// Google refuses login inside Playwright's bundled Chromium (it sees
    /// --enable-automation). Using the real Chrome/Edge channel and dropping that flag
    /// gets past the "browser may not be secure" block. Falls back to bundled Chromium
    /// if neither is installed (login will likely stay blocked there).
    /// </summary>
    private static async Task<IBrowserContext> LaunchPersistentAsync(
        IPlaywright pw, string profileDir, bool headless)
    {
        Directory.CreateDirectory(profileDir);
        Exception? last = null;
        foreach (var channel in new string?[] { "chrome", "msedge", null })
        {
            try
            {
                return await pw.Chromium.LaunchPersistentContextAsync(profileDir, new()
                {
                    Headless = headless,
                    Args = LaunchArgs,
                    IgnoreDefaultArgs = IgnoreArgs,
                    Channel = channel,
                });
            }
            catch (Exception e)
            {
                last = e;
            }
        }

        var msg = last?.Message ?? "unknown error";
        if (msg.Contains("Executable doesn't exist") || msg.Contains("playwright install"))
        {
            throw new MyMapsException(
                "No browser available for My Maps. The app couldn't find an installed " +
                "Chrome/Edge or download Chromium — check the internet connection and try " +
                $"again.\n\nDetails: {msg}", last!);
        }
        throw new MyMapsException($"Couldn't launch a browser for My Maps: {msg}", last!);
    }

    /// <summary>Is the saved profile signed in to My Maps?</summary>
    public async Task<bool> IsLoggedInAsync()
    {
        var page = await Context.NewPageAsync();
        try
        {
            await page.GotoAsync(AppConfig.MyMapsHomeUrl, new() { WaitUntil = WaitUntilState.Load });
            await page.WaitForTimeoutAsync(1500);
            return await page.GetByText(MyMapsSelectors.CreateNew).CountAsync() > 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private IBrowserContext Context =>
        _ctx ?? throw new MyMapsException("The My Maps browser session is not open.");

    /// <summary>
    /// One-time headed login: opens My Maps and waits for the user to sign in. The login
    /// is stored in the persistent profile, so later runs can be headless.
    /// </summary>
    public static async Task LoginAsync(
        string? profileDir = null, int timeoutSeconds = 300, Action<string>? log = null,
        CancellationToken ct = default)
    {
        await using var session = await StartAsync(profileDir, headless: false, log);
        var page = session.Context.Pages.FirstOrDefault() ?? await session.Context.NewPageAsync();
        await page.GotoAsync(AppConfig.MyMapsHomeUrl, new() { WaitUntil = WaitUntilState.Load });

        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await page.GetByText(MyMapsSelectors.CreateNew).CountAsync() > 0) return;
            await page.WaitForTimeoutAsync(2000);
        }
        throw new MyMapsException("Timed out waiting for the Google login.");
    }

    /// <summary>
    /// Clicks the first VISIBLE match for <paramref name="pattern"/>, trying
    /// button → link → text.
    ///
    /// My Maps renders the same label as different element types in different places
    /// (and keeps hidden template copies), so a single GetByText often resolves to a
    /// hidden node and times out. Polling several role strategies for a visible hit is
    /// far more robust.
    /// </summary>
    private static async Task<bool> ClickAsync(
        IPage scope, Regex pattern, int timeoutMs = 15000, bool optional = false)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var candidates = new Func<ILocator>[]
            {
                () => scope.GetByRole(AriaRole.Button, new() { NameRegex = pattern }),
                () => scope.GetByRole(AriaRole.Link, new() { NameRegex = pattern }),
                () => scope.GetByText(pattern),
            };
            foreach (var getter in candidates)
            {
                try
                {
                    var el = getter().First;
                    if (await el.IsVisibleAsync())
                    {
                        await el.ClickAsync();
                        return true;
                    }
                }
                catch
                {
                    // Try the next strategy.
                }
            }
            await scope.WaitForTimeoutAsync(300);
        }
        if (optional) return false;
        throw new MyMapsException($"Could not find a clickable element matching '{pattern}'.");
    }

    /// <summary>
    /// Frames belonging to the Google Picker (where the real upload input lives).
    /// Preferring these avoids setting the KML on some unrelated input[type=file] that
    /// happens to exist elsewhere — a wrong-input pick imports nothing, leaving the map empty.
    /// </summary>
    private static List<IFrame> PickerFrames(IPage page) =>
        page.Frames.Where(f =>
        {
            var u = (f.Url ?? "").ToLowerInvariant();
            return u.Contains("picker") || u.Contains("docs.google.com") || u.Contains("drive.google.com");
        }).ToList();

    /// <summary>
    /// Every frame, Picker frames first. The current My Maps 'Choose a file to import'
    /// dialog renders its Upload pane in the main document — outside the classic
    /// picker/docs iframes — so gating the search to Picker frames never finds it. We
    /// still try Picker frames first so the KML lands on the right input when the classic
    /// Picker is the one on screen.
    /// </summary>
    private static IEnumerable<IFrame> OrderedFrames(IPage page)
    {
        var picker = PickerFrames(page);
        return picker.Concat(page.Frames.Where(f => !picker.Contains(f)));
    }

    /// <summary>
    /// Sets the KML into the import dialog. Two shapes exist and Google flips between them:
    /// the classic Picker (a hidden input[type=file] we set directly), and the newer
    /// 'Choose a file to import' dialog that wires up its input only when 'Browse' is
    /// clicked (so we catch the resulting file chooser). Either way the dialog can open on
    /// the Drive/Recent tab on 2nd+ imports, so the 'Upload' tab is nudged every loop.
    /// </summary>
    private async Task<bool> SetKmlOnAnyFrameAsync(IPage page, string kmlPath, int timeoutMs = 30000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        var loggedTab = false;
        while (DateTime.UtcNow < deadline)
        {
            foreach (var frame in OrderedFrames(page))
            {
                try
                {
                    var input = await frame.QuerySelectorAsync("input[type=file]");
                    if (input is not null)
                    {
                        await input.SetInputFilesAsync(kmlPath);
                        await ConfirmPickerSelectionAsync(page);
                        return true;
                    }
                }
                catch
                {
                    // Frame may have navigated away mid-poll.
                }
            }

            // No pre-existing input: the newer dialog only creates it on 'Browse'. Click
            // Browse and set the KML on the file chooser it opens.
            if (await TryBrowseFileChooserAsync(page, kmlPath))
            {
                await ConfirmPickerSelectionAsync(page);
                return true;
            }

            foreach (var frame in OrderedFrames(page))
            {
                try
                {
                    var tab = frame.GetByText(MyMapsSelectors.UploadTab).First;
                    if (await tab.CountAsync() > 0 && await tab.IsVisibleAsync())
                    {
                        if (!loggedTab)
                        {
                            Log("clicking the import dialog's 'Upload' tab");
                            loggedTab = true;
                        }
                        await tab.ClickAsync(new() { Timeout = 1500 });
                        break;
                    }
                }
                catch
                {
                    // Keep polling.
                }
            }
            await page.WaitForTimeoutAsync(250);
        }
        Log($"no file input found; frames present: {string.Join(", ", page.Frames.Select(f => f.Url))}");
        return false;
    }

    /// <summary>
    /// Clicks the import dialog's 'Browse' button and sets the KML on the file chooser it
    /// opens. The newer 'Choose a file to import' dialog exposes no input[type=file] to
    /// set directly, so this is the only way in. Best-effort and short: returns false if
    /// no visible Browse button opens a chooser, so the caller keeps polling.
    /// </summary>
    private async Task<bool> TryBrowseFileChooserAsync(IPage page, string kmlPath)
    {
        foreach (var frame in OrderedFrames(page))
        {
            ILocator browse;
            try
            {
                browse = frame.GetByRole(AriaRole.Button, new() { NameRegex = MyMapsSelectors.Browse }).First;
                if (await browse.CountAsync() == 0 || !await browse.IsVisibleAsync()) continue;
            }
            catch
            {
                continue;
            }

            try
            {
                var chooser = await page.RunAndWaitForFileChooserAsync(
                    async () => await browse.ClickAsync(new() { Timeout = 2000 }),
                    new() { Timeout = 4000 });
                await chooser.SetFilesAsync(kmlPath);
                Log("set the KML via the 'Browse' file chooser");
                return true;
            }
            catch
            {
                // That button didn't open a chooser — try the next frame / next poll.
            }
        }
        return false;
    }

    /// <summary>
    /// Clicks the import dialog's "Select" button once the upload finishes. Some Picker
    /// variants no longer import on file-set — they upload, then wait on this button, so a
    /// run that only set the file hangs on the overlay. Best-effort: returns the moment
    /// the dialog is gone, so a variant that auto-imports adds no delay.
    /// </summary>
    private async Task ConfirmPickerSelectionAsync(IPage page, int timeoutMs = 15000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (!await PickerOpenAsync(page)) return; // dialog closed / import already fired
            foreach (var frame in OrderedFrames(page))
            {
                var getters = new Func<ILocator>[]
                {
                    () => frame.GetByRole(AriaRole.Button, new() { NameRegex = MyMapsSelectors.PickerSelect }),
                    () => frame.GetByText(MyMapsSelectors.PickerSelect),
                };
                foreach (var getter in getters)
                {
                    try
                    {
                        var el = getter().First;
                        if (await el.IsVisibleAsync() && await el.IsEnabledAsync())
                        {
                            Log("clicking the import dialog's 'Select' button");
                            await el.ClickAsync(new() { Timeout = 2000 });
                            return;
                        }
                    }
                    catch
                    {
                        // Not this frame/strategy — keep polling.
                    }
                }
            }
            await page.WaitForTimeoutAsync(300);
        }
    }

    /// <summary>Is the import dialog still on screen (waiting for a file)?</summary>
    private static async Task<bool> PickerOpenAsync(IPage page)
    {
        foreach (var frame in OrderedFrames(page))
        {
            try
            {
                if (await frame.GetByText(MyMapsSelectors.PickerOpen).First.IsVisibleAsync()) return true;
            }
            catch
            {
                // Frame gone — treat as closed.
            }
        }
        return false;
    }

    /// <summary>Is My Maps showing its 'Your action was reverted' error toast?</summary>
    private static async Task<bool> RevertedAsync(IPage page)
    {
        try
        {
            return await page.GetByText(MyMapsSelectors.Reverted).First.IsVisibleAsync();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Adapts a live editor page to the surface the import retry drives.</summary>
    private sealed class PageImportSurface(MyMapsSession session, IPage page, string kmlPath) : IImportSurface
    {
        public bool HasMid => page.Url.Contains("mid=");

        public Task ClickImportAsync() => ClickAsync(page, MyMapsSelectors.Import, timeoutMs: 20000);

        public Task<bool> SetFileAsync()
        {
            session.Log($"selecting KML file: {kmlPath}");
            return session.SetKmlOnAnyFrameAsync(page, kmlPath);
        }

        public Task<bool> IsPickerOpenAsync() => PickerOpenAsync(page);
        public Task<bool> IsRevertedAsync() => RevertedAsync(page);

        public async Task PressEscapeAsync()
        {
            try
            {
                await page.Keyboard.PressAsync("Escape");
            }
            catch
            {
                // Page already gone — nothing to dismiss.
            }
        }

        public async Task ReloadAsync()
        {
            try
            {
                await page.ReloadAsync(new() { WaitUntil = WaitUntilState.Load });
            }
            catch (Exception e)
            {
                session.Log($"reload after revert failed: {e.Message}");
            }
        }

        public Task DelayAsync(int milliseconds) => page.WaitForTimeoutAsync(milliseconds);
    }

    private Task DoImportAsync(IPage editor, string kmlPath) =>
        MyMapsImport.RunAsync(new PageImportSurface(this, editor, kmlPath), log: Log);

    /// <summary>Polls the page URL until the map id appears (set once the map saves).</summary>
    private static async Task<string?> WaitForMidAsync(IPage page, int timeoutMs = 60000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var m = MyMapsSelectors.Mid.Match(page.Url);
            if (m.Success) return m.Groups[1].Value;
            await page.WaitForTimeoutAsync(500);
        }
        return null;
    }

    /// <summary>First placemark name in the KML — used to confirm the import landed.</summary>
    internal static string? FirstPlacemarkName(string kmlPath)
    {
        try
        {
            XNamespace ns = "http://www.opengis.net/kml/2.2";
            var doc = XDocument.Load(kmlPath);
            foreach (var pm in doc.Descendants(ns + "Placemark"))
            {
                var name = pm.Element(ns + "name")?.Value.Trim();
                if (!string.IsNullOrEmpty(name)) return name;
            }
        }
        catch
        {
            return null;
        }
        return null;
    }

    /// <summary>
    /// Waits until the imported features render in the editor legend. The import runs
    /// asynchronously after the file input is set; mid can appear (map saved) while the
    /// placemarks are still loading — or never load if the import silently failed.
    /// </summary>
    private static async Task<bool> WaitForImportAsync(IPage page, string kmlPath, int timeoutMs = 40000)
    {
        var name = FirstPlacemarkName(kmlPath);
        if (string.IsNullOrEmpty(name))
        {
            await page.WaitForTimeoutAsync(3000);
            return true;
        }
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (await page.GetByText(name).First.IsVisibleAsync()) return true;
            }
            catch
            {
                // Keep polling.
            }
            if (await RevertedAsync(page)) return false; // rolled back; nothing will render
            await page.WaitForTimeoutAsync(500);
        }
        return false;
    }

    /// <summary>Opens My Maps home and clicks 'Create a new map'; returns the editor page.</summary>
    private async Task<IPage> OpenNewMapAsync()
    {
        var page = await Context.NewPageAsync();
        Log("opening My Maps home");
        await page.GotoAsync(AppConfig.MyMapsHomeUrl, new() { WaitUntil = WaitUntilState.Load });

        if (await page.GetByText(MyMapsSelectors.SignedOut).CountAsync() > 0
            && await page.GetByText(MyMapsSelectors.CreateNew).CountAsync() == 0)
        {
            throw new MyMapsException(
                "Not signed in to Google. Use 'Log in to Google' once and sign in.");
        }

        await page.WaitForTimeoutAsync(800); // let the home grid render
        Log("clicking 'Create a new map'");
        await ClickAsync(page, MyMapsSelectors.CreateNew, timeoutMs: 20000);

        // That opens a dialog whose confirm button is 'Create'. Some accounts skip
        // straight to the editor, so keep it optional with a short timeout.
        if (await ClickAsync(page, MyMapsSelectors.CreateConfirm, timeoutMs: 2500, optional: true))
            Log("dialog confirmed");

        try
        {
            await page.WaitForURLAsync(new Regex(@"/maps/d/.*edit"), new() { Timeout = 20000 });
        }
        catch
        {
            // Verified by the URL check below instead.
        }
        await page.WaitForLoadStateAsync(LoadState.Load);
        Log($"editor URL: {page.Url}");
        if (!page.Url.Contains("edit"))
            throw new MyMapsException($"Did not reach the map editor after creating a map (URL: {page.Url}).");
        return page;
    }

    /// <summary>
    /// The visible title text box of the 'Edit map title' dialog, or null. Scoped to the
    /// dialog first so we don't grab the map's place-search box by mistake.
    /// </summary>
    private static async Task<ILocator?> DialogTitleBoxAsync(IPage editor)
    {
        var candidates = new[]
        {
            editor.GetByRole(AriaRole.Dialog).GetByRole(AriaRole.Textbox),
            editor.Locator("input.navbar-form-input, .modal-dialog input[type=text]"),
            editor.GetByRole(AriaRole.Textbox),
        };
        foreach (var loc in candidates)
        {
            try
            {
                var el = loc.First;
                if (await el.IsVisibleAsync()) return el;
            }
            catch
            {
                // Try the next scope.
            }
        }
        return null;
    }

    /// <summary>Renames the map. Returns true once the tab title confirms the rename.</summary>
    private async Task<bool> SetTitleAsync(IPage editor, string title, int attempts = 2)
    {
        Log($"renaming map to: {title}");
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                if (MyMapsSelectors.MapNameFromTab(await editor.TitleAsync()) == title)
                {
                    Log("title updated");
                    return true;
                }

                var opened = false;
                foreach (var pattern in MyMapsSelectors.TitleClickTargets(await editor.TitleAsync()))
                {
                    if (await ClickAsync(editor, pattern, timeoutMs: 6000, optional: true))
                    {
                        opened = true;
                        break;
                    }
                }
                if (!opened)
                {
                    Log($"could not find the map title to click (attempt {attempt})");
                    await editor.WaitForTimeoutAsync(1500);
                    continue;
                }

                var box = await DialogTitleBoxAsync(editor);
                if (box is null)
                {
                    Log("title dialog did not expose a text box");
                    await editor.WaitForTimeoutAsync(1000);
                    continue;
                }
                await box.FillAsync(title);

                // Confirm: a Save/OK button if present, otherwise Enter commits the field.
                if (!await ClickAsync(editor, MyMapsSelectors.Save, timeoutMs: 5000, optional: true))
                    await box.PressAsync("Enter");
                await editor.WaitForTimeoutAsync(1500);

                if (MyMapsSelectors.MapNameFromTab(await editor.TitleAsync()) == title)
                {
                    Log("title updated");
                    return true;
                }
                Log($"title still '{await editor.TitleAsync()}' after attempt {attempt}");
            }
            catch (Exception e)
            {
                Log($"title change failed: {e.Message}");
            }
        }
        return false;
    }

    /// <summary>Creates a new map, imports the KML, names it. Throws MyMapsException on failure.</summary>
    public async Task<CreatedMap> CreateMapFromKmlAsync(string kmlPath, string title, CancellationToken ct = default)
    {
        if (_mapsCreated > 0)
        {
            // Creating maps back to back is what makes Google reject the second import
            // with "Your action was reverted"; a short breather avoids it.
            Log("pausing before the next map");
            await Task.Delay(TimeSpan.FromSeconds(AppConfig.MapGapSeconds), ct);
        }

        var editor = await OpenNewMapAsync();
        _mapsCreated++;
        try
        {
            await editor.WaitForLoadStateAsync(LoadState.DOMContentLoaded);
            await editor.WaitForTimeoutAsync(800);
            await DoImportAsync(editor, kmlPath);

            Log("file submitted; waiting for the map to save (mid in URL)");
            var mid = await WaitForMidAsync(editor, timeoutMs: 90000)
                ?? throw new MyMapsException("Timed out waiting for the map to be created (no mid in URL).");
            Log($"map created: mid={mid}");

            // mid can appear while the placemarks are still loading — or never load if the
            // import silently failed. Retry once: a re-import into the same (already saved)
            // map is the cheapest recovery for a dropped first attempt.
            if (!await WaitForImportAsync(editor, kmlPath))
            {
                Log("no features after import — retrying the import once");
                await DumpScreenshotAsync(editor, Path.ChangeExtension(kmlPath, ".empty1.png"));
                await DoImportAsync(editor, kmlPath);
                if (!await WaitForImportAsync(editor, kmlPath))
                    Log("WARNING: still no imported features after retry; map may be empty");
            }

            Log("setting the map title");
            if (!await SetTitleAsync(editor, title))
                Log("WARNING: map left as 'Untitled map' (title step failed)");
            Log("done");
            return new CreatedMap(title, editor.Url, mid);
        }
        catch (MyMapsException)
        {
            await DumpScreenshotAsync(editor, Path.ChangeExtension(kmlPath, ".error.png"));
            throw;
        }
        catch (Exception e)
        {
            await DumpScreenshotAsync(editor, Path.ChangeExtension(kmlPath, ".error.png"));
            throw new MyMapsException($"My Maps automation failed for {kmlPath}: {e.Message}", e);
        }
        finally
        {
            // Close this editor tab so the next map starts clean — leaving prior My Maps
            // editors open (each with a live Picker/OAuth session) can make a later import
            // land on the wrong map or no-op, leaving those maps empty.
            try
            {
                await editor.CloseAsync();
            }
            catch
            {
                // Already gone.
            }
        }
    }

    private static async Task DumpScreenshotAsync(IPage page, string path)
    {
        try
        {
            await page.ScreenshotAsync(new() { Path = path, FullPage = true });
        }
        catch
        {
            // Debug aid only.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_ctx is not null) await _ctx.CloseAsync();
        _pw?.Dispose();
    }
}
