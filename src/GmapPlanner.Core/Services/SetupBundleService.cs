using System.Text.Json;
using System.Text.Json.Nodes;

namespace GmapPlanner.Core.Services;

/// <summary>
/// Ports appconfig.py's apply_setup_bundle: merge a single setup JSON into the local
/// config plus the Drive credentials.json. Only keys present (and non-empty) are written;
/// anything missing keeps its current value.
/// </summary>
public static class SetupBundleService
{
    // Bundle keys that map to the Drive OAuth client file, not config.json.
    private static readonly string[] CredentialAliases = ["credentials", "credentials.json", "DRIVE_CREDENTIALS"];

    public record BundleResult(List<string> Applied)
    {
        public bool AnythingApplied => Applied.Count > 0;
    }

    /// <summary>The outcome of merging a bundle into settings, without touching disk.</summary>
    public sealed record BundleMerge(AppSettings Settings, string? CredentialsJson, List<string> Applied);

    /// <summary>
    /// Merges a bundle into a copy of <paramref name="current"/>. Pure: the caller decides where
    /// the settings and credentials.json go (a file on desktop, the browser's storage).
    /// </summary>
    public static BundleMerge Merge(JsonObject bundle, AppSettings current)
    {
        var settings = current with { };
        var applied = new List<string>();

        if (TryText(bundle, "GOOGLE_API_KEY", out var gemini)) { settings.GoogleApiKey = gemini; applied.Add("GOOGLE_API_KEY"); }
        if (TryText(bundle, "GEO_API_KEY", out var geo)) { settings.GeoApiKey = geo; applied.Add("GEO_API_KEY"); }
        if (TryText(bundle, "GCP_SA_JSON", out var sa)) { settings.GcpSaJson = sa; applied.Add("GCP_SA_JSON"); }
        if (TryText(bundle, "ANALYTICS_SHEET_ID", out var sheet)) { settings.AnalyticsSheetId = sheet; applied.Add("ANALYTICS_SHEET_ID"); }

        // Drive OAuth client credentials.json is a file, not a config key.
        string? credentials = null;
        foreach (var alias in CredentialAliases)
        {
            if (!bundle.TryGetPropertyValue(alias, out var node) || node is null) continue;
            var text = AsText(node);
            if (string.IsNullOrWhiteSpace(text)) continue;
            try
            {
                JsonNode.Parse(text); // only real JSON counts
                credentials = text;
                applied.Add("credentials.json");
            }
            catch
            {
                // Not valid JSON — skip rather than hand back a corrupt file.
            }
            break;
        }

        return new BundleMerge(settings, credentials, applied);
    }

    /// <summary>Parses bundle text and merges it. Throws on invalid JSON (not a JSON object).</summary>
    public static BundleMerge MergeFromText(string text, AppSettings current) => Merge(ParseBundle(text), current);

    /// <summary>Desktop: merges into config.json and writes credentials.json. Returns the fields actually written.</summary>
    public static BundleResult Apply(JsonObject bundle)
    {
        var merged = Merge(bundle, AppSettingsService.Load());
        AppSettingsService.Save(merged.Settings);

        var applied = merged.Applied;
        if (merged.CredentialsJson is not null)
        {
            try
            {
                File.WriteAllText(AppConfig.DriveCredentialsFile, merged.CredentialsJson);
            }
            catch
            {
                applied.Remove("credentials.json"); // not written, so not applied
            }
        }
        return new BundleResult(applied);
    }

    /// <summary>Parses bundle text and applies it. Throws on invalid JSON (not a JSON object).</summary>
    public static BundleResult ApplyFromText(string text) => Apply(ParseBundle(text));

    private static JsonObject ParseBundle(string text)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException e)
        {
            throw new ArgumentException($"Not a valid setup file: {e.Message}", e);
        }
        return node as JsonObject ?? throw new ArgumentException("The setup file must contain a JSON object.");
    }

    private static bool TryText(JsonObject bundle, string key, out string value)
    {
        value = "";
        if (!bundle.TryGetPropertyValue(key, out var node) || node is null) return false;
        var text = AsText(node);
        if (string.IsNullOrWhiteSpace(text)) return false; // present but empty → keep current
        value = text.Trim();
        return true;
    }

    /// <summary>A bundle value as text: JSON objects are re-serialized, scalars stringified.</summary>
    private static string AsText(JsonNode node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node.ToJsonString();
}
