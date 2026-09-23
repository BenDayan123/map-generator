using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using GmapPlanner.Core.Json;

namespace GmapPlanner.Core.Services;

/// <summary>
/// In-app self-update via GitHub Releases. Ports the Python original's updater.py:
/// check the repo's latest release, compare its tag to the running version, and (on
/// demand) download the matching installer and launch it to update in place.
///
/// Apply strategy (deliberately simple, matching the original):
/// - Windows: run the Inno Setup installer with /SILENT; it closes the app, updates the
///   files, and (via installer.iss [Run] Check:WizardSilent) relaunches it.
/// - macOS: a detached script waits for the app to quit, mounts the downloaded .dmg,
///   replaces the running .app bundle with the new one, and relaunches it — the same
///   hands-off update as Windows. Outside an .app (a dev run) it just opens the .dmg.
///
/// Everything is best-effort: any network/parse failure returns null so a missing
/// connection can never break the app.
/// </summary>
public class UpdateService(HttpClient http)
{
    private const string ApiLatest = "https://api.github.com/repos/{0}/releases/latest";
    private const string UserAgent = "map-generator-updater";

    /// <summary>The running app's version as "major.minor.build" (e.g. "1.0.0").</summary>
    public static string CurrentVersion() =>
        Assembly.GetEntryAssembly()?.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.0.0";

    /// <summary>
    /// Queries the latest GitHub release. Returns info for any version (callers use
    /// <see cref="UpdateInfo.HasUpdate"/> to decide) or null on any failure — a missing OS
    /// asset is a different state from unreachable, so the UI can be honest about which.
    /// </summary>
    public async Task<UpdateInfo?> CheckForUpdateAsync(string repo, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, string.Format(ApiLatest, repo));
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct);
            var rel = JsonSerializer.Deserialize(json, GmapPlannerJsonContext.Default.GithubRelease);
            if (rel is null) return null;

            var asset = PickAsset(rel.Assets ?? []);
            return new UpdateInfo(
                Current: CurrentVersion(),
                Latest: (rel.TagName ?? "").TrimStart('v', 'V'),
                Notes: rel.Body ?? "",
                HtmlUrl: rel.HtmlUrl ?? "",
                AssetName: asset?.Name ?? "",
                AssetUrl: asset?.BrowserDownloadUrl ?? "");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Picks the release asset for this OS + CPU (.exe on Windows, arm64 .dmg on macOS).</summary>
    internal static GithubAsset? PickAsset(IReadOnlyList<GithubAsset> assets)
    {
        if (OperatingSystem.IsWindows())
            return assets.FirstOrDefault(a => (a.Name ?? "").EndsWith(".exe", StringComparison.OrdinalIgnoreCase));

        if (OperatingSystem.IsMacOS())
        {
            var dmgs = assets.Where(a => (a.Name ?? "").EndsWith(".dmg", StringComparison.OrdinalIgnoreCase)).ToList();
            // Prefer an arm64/universal build; only fall back to any .dmg if none is tagged.
            return dmgs.FirstOrDefault(a =>
                       (a.Name ?? "").Contains("arm64", StringComparison.OrdinalIgnoreCase) ||
                       (a.Name ?? "").Contains("universal", StringComparison.OrdinalIgnoreCase))
                   ?? dmgs.FirstOrDefault();
        }
        return null;
    }

    /// <summary>Streams a release asset to a temp file, reporting 0..1 progress. Returns the local path.</summary>
    public async Task<string> DownloadAssetAsync(string url, string name, Action<double>? progress = null, CancellationToken ct = default)
    {
        var dest = Path.Combine(Path.GetTempPath(), name);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength ?? 0;
        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using var fs = File.Create(dest);
        var buffer = new byte[1 << 16];
        long done = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, ct)) > 0)
        {
            await fs.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            if (progress is not null && total > 0) progress(Math.Min((double)done / total, 1.0));
        }
        return dest;
    }

    public static bool IsSelfUpdateSupported => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    /// <summary>
    /// Launches the downloaded installer to update in place, then quits the app so its files
    /// can be replaced. Windows: the Inno installer runs /SILENT and relaunches the new
    /// version. macOS: <see cref="MacSwapScript"/> swaps the .app bundle and relaunches it.
    /// </summary>
    public static void ApplyUpdate(string installerPath)
    {
        if (OperatingSystem.IsWindows())
        {
            // UseShellExecute detaches the installer so it outlives the app we're about to exit.
            Process.Start(new ProcessStartInfo(installerPath, "/SILENT /SUPPRESSMSGBOXES /NORESTART")
            {
                UseShellExecute = true,
            });
            Environment.Exit(0);
        }
        else if (OperatingSystem.IsMacOS())
        {
            if (MacBundlePath(AppContext.BaseDirectory) is not { } bundle)
            {
                // Not running from an .app (dev run): nothing to swap, hand over the .dmg.
                Process.Start(new ProcessStartInfo("open", $"\"{installerPath}\"") { UseShellExecute = false });
                return;
            }
            var script = Path.Combine(Path.GetTempPath(), "gmapplanner-update.sh");
            File.WriteAllText(script, MacSwapScript(installerPath, bundle, Environment.ProcessId));
            // nohup + a new session so the script outlives this process.
            Process.Start(new ProcessStartInfo("/bin/bash")
            {
                ArgumentList = { "-c", $"nohup /bin/bash {Quote(script)} >/dev/null 2>&1 &" },
                UseShellExecute = false,
            });
            Environment.Exit(0);
        }
        else
        {
            throw new PlatformNotSupportedException("Self-update is only supported on Windows and macOS.");
        }
    }

    /// <summary>The enclosing <c>X.app</c> of an app running from <c>X.app/Contents/MacOS/</c>, else null.</summary>
    public static string? MacBundlePath(string baseDirectory)
    {
        var macOs = baseDirectory.TrimEnd('/');
        var contents = Path.GetDirectoryName(macOs);
        var bundle = contents is null ? null : Path.GetDirectoryName(contents);
        return Path.GetFileName(macOs) == "MacOS" && Path.GetFileName(contents) == "Contents"
               && bundle is not null && bundle.EndsWith(".app", StringComparison.Ordinal)
            ? bundle.Replace('\\', '/')
            : null;
    }

    /// <summary>
    /// Bash that waits for <paramref name="pid"/> to exit, mounts <paramref name="dmgPath"/>,
    /// replaces <paramref name="bundlePath"/> with the .app inside it, and relaunches it.
    /// The .dmg came from our own HttpClient download, so it carries no quarantine flag.
    /// </summary>
    public static string MacSwapScript(string dmgPath, string bundlePath, int pid) => $$"""
        #!/bin/bash
        while kill -0 {{pid}} 2>/dev/null; do sleep 0.5; done
        MNT="$(mktemp -d)"
        hdiutil attach {{Quote(dmgPath)}} -nobrowse -quiet -mountpoint "$MNT" || { open {{Quote(dmgPath)}}; exit 1; }
        NEW="$(find "$MNT" -maxdepth 1 -name '*.app' | head -n 1)"
        rm -rf {{Quote(bundlePath + ".new")}}
        if [ -n "$NEW" ] && ditto "$NEW" {{Quote(bundlePath + ".new")}}; then
          rm -rf {{Quote(bundlePath)}}
          mv {{Quote(bundlePath + ".new")}} {{Quote(bundlePath)}}
        else
          rm -rf {{Quote(bundlePath + ".new")}}
          hdiutil detach "$MNT" -quiet
          open {{Quote(dmgPath)}}
          exit 1
        fi
        hdiutil detach "$MNT" -quiet
        open {{Quote(bundlePath)}}
        """;

    // $$""" so the bash braces stay literal; {{x}} interpolates.
    /// <summary>Single-quote for bash: 'it'\''s' survives spaces, quotes and $.</summary>
    private static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    /// <summary>True if <paramref name="latest"/> is a strictly higher version than <paramref name="current"/>.</summary>
    public static bool IsNewer(string latest, string current)
    {
        var a = ParseVer(latest);
        var b = ParseVer(current);
        var n = Math.Max(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            var av = i < a.Length ? a[i] : 0;
            var bv = i < b.Length ? b[i] : 0;
            if (av != bv) return av > bv;
        }
        return false;
    }

    /// <summary>"v1.2.0" -> [1, 2, 0]. Non-numeric leading chars per part degrade to 0.</summary>
    private static int[] ParseVer(string s)
    {
        s = (s ?? "").Trim().TrimStart('v', 'V');
        return s.Split('.').Select(part =>
        {
            var digits = new string(part.TakeWhile(char.IsDigit).ToArray());
            return digits.Length > 0 ? int.Parse(digits) : 0;
        }).ToArray();
    }
}

/// <summary>Result of an update check. Reachable release info; may or may not be newer.</summary>
public sealed record UpdateInfo(string Current, string Latest, string Notes, string HtmlUrl, string AssetName, string AssetUrl)
{
    public bool HasUpdate => UpdateService.IsNewer(Latest, Current);

    /// <summary>Whether the latest release actually ships an installer for this OS.</summary>
    public bool HasAsset => AssetUrl.Length > 0;
}

// --- GitHub Releases API DTOs (only the fields we read) ----------------------
internal sealed class GithubRelease
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    [JsonPropertyName("assets")] public List<GithubAsset>? Assets { get; set; }
}

internal sealed class GithubAsset
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
}
