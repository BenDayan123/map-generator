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

    /// <summary>Applies a parsed setup bundle. Returns the fields actually written.</summary>
    public static BundleResult Apply(JsonObject bundle)
    {
        var settings = AppSettingsService.Load();
        var applied = new List<string>();

        if (TryText(bundle, "GOOGLE_API_KEY", out var gemini)) { settings.GoogleApiKey = gemini; applied.Add("GOOGLE_API_KEY"); }
        if (TryText(bundle, "GEO_API_KEY", out var geo)) { settings.GeoApiKey = geo; applied.Add("GEO_API_KEY"); }
        if (TryText(bundle, "GCP_SA_JSON", out var sa)) { settings.GcpSaJson = sa; applied.Add("GCP_SA_JSON"); }
        AppSettingsService.Save(settings);

        // Drive OAuth client credentials.json lives as a file, not a config key.
        foreach (var alias in CredentialAliases)
        {
            if (!bundle.TryGetPropertyValue(alias, out var node) || node is null) continue;
            var text = AsText(node);
            if (string.IsNullOrWhiteSpace(text)) continue;
            try
            {
                JsonNode.Parse(text); // validate it's real JSON before writing
                File.WriteAllText(AppConfig.DriveCredentialsFile, text);
                applied.Add("credentials.json");
            }
            catch
            {
                // Not valid JSON — skip rather than corrupt the file.
            }
            break;
        }

        return new BundleResult(applied);
    }

    /// <summary>Parses bundle text and applies it. Throws on invalid JSON (not a JSON object).</summary>
    public static BundleResult ApplyFromText(string text)
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
        if (node is not JsonObject obj)
            throw new ArgumentException("The setup file must contain a JSON object.");
        return Apply(obj);
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
