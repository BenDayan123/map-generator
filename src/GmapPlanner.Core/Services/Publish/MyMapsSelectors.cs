using System.Text.RegularExpressions;

namespace GmapPlanner.Core.Services.Publish;

/// <summary>
/// Centralized selectors — the bit Google breaks. Ported from mymaps.py's SEL_*
/// constants. The editor is always opened with hl=en so these English patterns hold.
/// </summary>
public static partial class MyMapsSelectors
{
    public static readonly Regex CreateNew = new("create a new map", RegexOptions.IgnoreCase);

    /// <summary>Home flow: 'Create a new map' opens a dialog whose confirm button is 'Create'.</summary>
    public static readonly Regex CreateConfirm = new(@"^\s*create\s*$", RegexOptions.IgnoreCase);

    public static readonly Regex Import = new(@"^\s*import\s*$", RegexOptions.IgnoreCase);
    public static readonly Regex UntitledMap = new("untitled map", RegexOptions.IgnoreCase);
    public static readonly Regex Save = new(@"^\s*(save|ok|done)\s*$", RegexOptions.IgnoreCase);
    public static readonly Regex UploadTab = new(@"^\s*upload\s*$", RegexOptions.IgnoreCase);

    /// <summary>
    /// The Picker's confirm button. The current Google Picker no longer imports the
    /// moment the file input is set — after the upload it waits on this "Select" button.
    /// Older Pickers auto-closed, so clicking it is best-effort.
    /// </summary>
    public static readonly Regex PickerSelect = new(@"^\s*(select|open)\s*$", RegexOptions.IgnoreCase);

    /// <summary>
    /// The 'Browse' button in the newer 'Choose a file to import' dialog. It only wires up
    /// an input[type=file] when clicked, so we catch the file chooser it opens.
    /// </summary>
    public static readonly Regex Browse = new(@"^\s*browse\s*$", RegexOptions.IgnoreCase);

    /// <summary>
    /// Text the Picker's upload pane shows while it waits for a file — i.e. "the drag
    /// and drop dialog is still open".
    /// </summary>
    public static readonly Regex PickerOpen =
        new("drag (and drop|files here)|select a file from your|choose a file to import",
            RegexOptions.IgnoreCase);

    /// <summary>
    /// My Maps' red toast when it refuses a save/import — usually because the editor's
    /// session went stale or Google is rate-limiting rapid map creation.
    /// </summary>
    public static readonly Regex Reverted =
        new("action was reverted|couldn't save|could not save|something went wrong", RegexOptions.IgnoreCase);

    /// <summary>Text shown only when signed OUT — used to detect login state.</summary>
    public static readonly Regex SignedOut = new("sign in", RegexOptions.IgnoreCase);

    /// <summary>A My Maps edit URL carries the map id as ?mid=... / &amp;mid=...</summary>
    public static readonly Regex Mid = new(@"[?&]mid=([^&#]+)");

    /// <summary>
    /// The browser tab is named "&lt;map name&gt; - Google My Maps", the one place the
    /// current map name can be read without guessing at Google's DOM.
    /// </summary>
    public static readonly Regex TabSuffix =
        new(@"\s*[-–—|]\s*Google (My )?Maps\s*$", RegexOptions.IgnoreCase);

    /// <summary>The map's current name, taken from the browser tab title.</summary>
    public static string MapNameFromTab(string? tabTitle) =>
        TabSuffix.Replace(tabTitle ?? "", "").Trim();

    /// <summary>
    /// Patterns to click to open the title dialog, best guess first.
    ///
    /// Importing a KML into a fresh map makes My Maps rename it after the file, so
    /// 'Untitled map' is frequently already gone by the time the rename runs — which is
    /// why some maps kept the file name. The current name from the tab title is the
    /// reliable target; the literal 'Untitled map' stays as a fallback.
    /// </summary>
    public static List<Regex> TitleClickTargets(string? tabTitle)
    {
        var name = MapNameFromTab(tabTitle);
        var targets = new List<Regex>();
        if (name.Length > 0 && !UntitledMap.IsMatch(name))
            targets.Add(new Regex($@"^\s*{Regex.Escape(name)}\s*$"));
        targets.Add(UntitledMap);
        return targets;
    }
}
