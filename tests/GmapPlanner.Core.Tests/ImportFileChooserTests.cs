using GmapPlanner.Core.Services.Publish;
using Microsoft.Playwright;
using Xunit;

namespace GmapPlanner.Core.Tests;

/// <summary>
/// Integration check for the mechanism the My Maps import now relies on: the newer
/// 'Choose a file to import' dialog exposes no input[type=file] to set directly, so the
/// KML has to go in by clicking 'Browse' and setting the file on the chooser it opens.
///
/// This drives a real headless Chromium against a faithful replica of that dialog —
/// a Browse button that click-proxies a hidden input, exactly as My Maps renders it — to
/// prove the Browse locator finds it and the file lands. Auto-skips when no browser can
/// launch (e.g. CI without Chromium), so it never fails the suite for the wrong reason.
/// </summary>
public class ImportFileChooserTests
{
    private const string DialogHtml = """
        <!doctype html><html><body>
          <div id="dlg">
            <h2>Choose a file to import</h2>
            <button id="browse" onclick="document.getElementById('f').click()">Browse</button>
            <p>or drag files here</p>
            <input id="f" type="file" style="display:none"
                   onchange="window.__picked = this.files.length ? this.files[0].name : ''">
          </div>
        </body></html>
        """;

    [Fact]
    public async Task Browse_button_opens_a_file_chooser_that_sets_the_kml()
    {
        IPlaywright? pw = null;
        IBrowser? browser = null;
        try
        {
            Microsoft.Playwright.Program.Main(["install", "chromium"]);
            pw = await Playwright.CreateAsync();
            browser = await pw.Chromium.LaunchAsync(new() { Headless = true });
        }
        catch
        {
            pw?.Dispose();
            return; // No browser in this environment — skip rather than fail.
        }

        try
        {
            var page = await browser.NewPageAsync();
            await page.SetContentAsync(DialogHtml);

            var kml = Path.Combine(Path.GetTempPath(), $"import_chooser_{Guid.NewGuid():N}.kml");
            await File.WriteAllTextAsync(kml, "<kml/>");

            // The exact path SetKmlOnAnyFrameAsync takes for the newer dialog.
            var browse = page.GetByRole(AriaRole.Button, new() { NameRegex = MyMapsSelectors.Browse });
            var chooser = await page.RunAndWaitForFileChooserAsync(async () => await browse.First.ClickAsync());
            await chooser.SetFilesAsync(kml);

            await page.WaitForFunctionAsync("() => window.__picked !== undefined");
            var picked = await page.EvaluateAsync<string>("() => window.__picked");

            Assert.Equal(Path.GetFileName(kml), picked);
            File.Delete(kml);
        }
        finally
        {
            await browser.CloseAsync();
            pw.Dispose();
        }
    }
}
