using System.Globalization;
using System.Text.RegularExpressions;

namespace GmapPlanner.Core.Services;

/// <summary>What is happening to one map during a My Maps publish.</summary>
public enum MapStep { Creating, Sharing, Created, Failed }

/// <summary>
/// The per-map progress lines <c>PublishService</c> reports ("Creating map 2/3: …"). They live in
/// Core so the UI (which can't reference Core.Publish) can turn them back into per-map state —
/// the same text arrives from the desktop publisher and, relayed, from the cloud worker.
/// </summary>
public static partial class PublishProgress
{
    public static string Creating(int index, int total, string title) => $"Creating map {index + 1}/{total}: {title}";
    public static string Sharing(int index, int total) => $"Sharing map {index + 1}/{total}";
    public static string Created(int index, int total) => $"Created map {index + 1}/{total}";
    public static string Failed(int index, int total) => $"Map {index + 1}/{total} failed";

    /// <summary>The 0-based map index and step a progress line describes, or null for any other line.</summary>
    public static (int Index, MapStep Step)? Parse(string line)
    {
        var m = LineRegex().Match(line);
        if (!m.Success) return null;
        var index = int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture) - 1;
        var step = m.Groups["kind"].Value switch
        {
            "Creating map " => MapStep.Creating,
            "Sharing map " => MapStep.Sharing,
            "Created map " => MapStep.Created,
            _ => MapStep.Failed,
        };
        return (index, step);
    }

    [GeneratedRegex(@"^(?:(?<kind>Creating map )(?<n>\d+)/\d+: .*|(?<kind>Sharing map |Created map )(?<n>\d+)/\d+|(?<kind>Map )(?<n>\d+)/\d+ failed)$")]
    private static partial Regex LineRegex();
}
