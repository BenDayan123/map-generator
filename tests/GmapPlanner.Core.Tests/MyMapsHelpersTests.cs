using System.Diagnostics;
using GmapPlanner.Core.Services.Publish;
using Xunit;

namespace GmapPlanner.Core.Tests;

/// <summary>
/// Ports gmap_planner/test_mymaps_helpers.py. Covers the two things that made
/// publishing flaky: which element is clicked to open the rename dialog (a wrong guess
/// leaves the map named after the KML file), and the import retry that has to escape a
/// Picker dialog left open by a failed upload.
/// </summary>
public class MyMapsHelpersTests
{
    [Theory]
    [InlineData("Untitled map - Google My Maps", "Untitled map")]
    [InlineData("1-5 - Google My Maps", "1-5")]
    [InlineData("רונן ומאיה — Days 1-5 - Google My Maps", "רונן ומאיה — Days 1-5")]
    [InlineData("", "")]
    public void MapNameFromTab_StripsTheGoogleSuffix(string tab, string expected)
    {
        Assert.Equal(expected, MyMapsSelectors.MapNameFromTab(tab));
    }

    [Fact]
    public void TitleClickTargets_PutsTheCurrentNameFirst()
    {
        // A map auto-named after the KML file: that name must be the first target,
        // otherwise the rename never opens the dialog.
        var targets = MyMapsSelectors.TitleClickTargets("1-5 - Google My Maps");

        Assert.Matches(targets[0], "1-5");
        Assert.Same(MyMapsSelectors.UntitledMap, targets[^1]);
    }

    [Theory]
    [InlineData("Untitled map - Google My Maps")]
    [InlineData("")]
    public void TitleClickTargets_StillUntitled_IsJustTheFallback(string tab)
    {
        var targets = MyMapsSelectors.TitleClickTargets(tab);

        Assert.Same(MyMapsSelectors.UntitledMap, Assert.Single(targets));
    }

    [Fact]
    public void TitleClickTargets_EscapesRegexMetacharactersInATripName()
    {
        var targets = MyMapsSelectors.TitleClickTargets("Trip (2026) [draft] - Google My Maps");

        Assert.Matches(targets[0], "Trip (2026) [draft]");
    }

    [Fact]
    public async Task Import_RetriesPastAStuckDialog()
    {
        var surface = new FakeImportSurface(stickyUploads: 1);

        await MyMapsImport.RunAsync(surface, closeTimeoutMs: 100);

        Assert.Equal(2, surface.Uploads);      // retried the upload
        Assert.Equal(2, surface.ImportClicks); // clicked Import again for the retry
        Assert.True(surface.Escapes >= 1);     // escaped the stuck dialog first
        Assert.False(surface.PickerOpen);
    }

    [Fact]
    public async Task Import_RecoversFromARevertedAction()
    {
        // First upload is reverted by My Maps; the dialog stays open behind the toast.
        var surface = new FakeImportSurface(stickyUploads: 1, reverts: 1);
        var sw = Stopwatch.StartNew();

        await MyMapsImport.RunAsync(surface, closeTimeoutMs: 100);

        Assert.Equal(2, surface.Uploads);  // retried after the revert
        Assert.Equal(1, surface.Reloads);  // reloaded the editor to re-sync
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2)); // bailed out instead of waiting
    }

    [Fact]
    public async Task Import_RevertBeforeTheMapExists_DoesNotReload()
    {
        // No mid yet: reloading would land on a fresh empty editor, so it must not.
        var surface = new FakeImportSurface(stickyUploads: 1, reverts: 1, hasMid: false);

        await MyMapsImport.RunAsync(surface, closeTimeoutMs: 100);

        Assert.Equal(0, surface.Reloads);
    }

    [Fact]
    public async Task Import_DialogNeverCloses_Throws()
    {
        var surface = new FakeImportSurface(stickyUploads: 99);

        await Assert.ThrowsAsync<MyMapsException>(
            () => MyMapsImport.RunAsync(surface, closeTimeoutMs: 50));
    }

    [Fact]
    public async Task Import_SucceedsWhenImportFiresButPickerLingers()
    {
        // The Picker variant that imports the file yet never auto-closes (resets to the
        // drag view). The import took — so it must succeed without retrying, then dismiss
        // the lingering dialog so the rename that follows isn't blocked.
        var surface = new FakeImportSurface(stickyUploads: 99, imported: true);

        await MyMapsImport.RunAsync(surface, closeTimeoutMs: 100);

        Assert.Equal(1, surface.Uploads);   // no retry — the first import already landed
        Assert.True(surface.Escapes >= 1);  // dismissed the lingering Picker
        Assert.False(surface.PickerOpen);
    }

    /// <summary>Editor page stand-in: the Picker dialog sticks open after the first uploads.</summary>
    private sealed class FakeImportSurface(
        int stickyUploads, int reverts = 0, bool hasMid = true, bool imported = false) : IImportSurface
    {
        public int Uploads { get; private set; }
        public int Escapes { get; private set; }
        public int ImportClicks { get; private set; }
        public int Reloads { get; private set; }
        public bool PickerOpen { get; private set; }
        public bool HasMid => hasMid;

        public Task ClickImportAsync()
        {
            ImportClicks++;
            return Task.CompletedTask;
        }

        public Task<bool> SetFileAsync()
        {
            Uploads++;
            PickerOpen = Uploads <= stickyUploads;
            return Task.FromResult(true);
        }

        public Task<bool> IsPickerOpenAsync() => Task.FromResult(PickerOpen);
        public Task<bool> IsRevertedAsync() => Task.FromResult(Uploads <= reverts);
        public Task<bool> IsImportedAsync() => Task.FromResult(imported && Uploads > 0);

        public Task PressEscapeAsync()
        {
            Escapes++;
            PickerOpen = false;
            return Task.CompletedTask;
        }

        public Task ReloadAsync()
        {
            Reloads++;
            PickerOpen = false;
            return Task.CompletedTask;
        }

        // Tests must not actually sleep out the retry backoffs.
        public Task DelayAsync(int milliseconds) => Task.CompletedTask;
    }
}
