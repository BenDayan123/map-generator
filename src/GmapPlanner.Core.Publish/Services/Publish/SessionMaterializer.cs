using System.Text.Json;

namespace GmapPlanner.Core.Services.Publish;

/// <summary>
/// Bridges a <see cref="SessionFile"/>'s opaque Drive blobs to the on-disk layout
/// <see cref="DriveShareService.CreateAsync"/> expects, and back. The token file name is Google's
/// FileDataStore convention (<c>{TokenResponse.FullName}-{key}</c>, key "user"), pinned by a test.
/// </summary>
public static class SessionMaterializer
{
    public const string DriveTokenFileName = "Google.Apis.Auth.OAuth2.Responses.TokenResponse-user";

    /// <summary>
    /// Writes credentials.json and the cached Drive token so
    /// <c>DriveShareService.CreateAsync(credentialsPath, tokenDir)</c> authenticates with no browser.
    /// </summary>
    public static void WriteDriveCreds(SessionFile session, string credentialsPath, string tokenDir)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(credentialsPath))!);
        Directory.CreateDirectory(tokenDir);
        File.WriteAllText(credentialsPath, session.DriveCredentials.GetRawText());
        File.WriteAllText(Path.Combine(tokenDir, DriveTokenFileName), session.DriveToken.GetRawText());
    }

    /// <summary>Reads back the cached Drive token a login left in <paramref name="tokenDir"/> (for the LoginHelper).</summary>
    public static JsonElement CaptureDriveToken(string tokenDir)
    {
        var file = Path.Combine(tokenDir, DriveTokenFileName);
        if (!File.Exists(file))
        {
            // Robust to a FileDataStore naming change: take whatever token file is there.
            file = Directory.EnumerateFiles(tokenDir).FirstOrDefault(f => f.Contains("TokenResponse"))
                ?? throw new FileNotFoundException($"No cached Drive token in {tokenDir} after login.");
        }
        return JsonDocument.Parse(File.ReadAllText(file)).RootElement.Clone();
    }
}
