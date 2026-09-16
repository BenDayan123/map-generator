using System.Text.Json;
using GmapPlanner.Core.Services.Publish;
using Xunit;

namespace GmapPlanner.Core.Tests;

public class SessionMaterializerTests
{
    [Fact]
    public void WriteDriveCreds_writes_credentials_and_the_filedatastore_token()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sm-" + Guid.NewGuid().ToString("N"));
        var tokenDir = Path.Combine(dir, "drive-token");
        var credsPath = Path.Combine(dir, "credentials.json");
        try
        {
            var session = new SessionFile(
                1,
                Parse("""{"cookies":[]}"""),
                Parse("""{"installed":{"client_id":"x"}}"""),
                Parse("""{"access_token":"a","refresh_token":"r"}"""));

            SessionMaterializer.WriteDriveCreds(session, credsPath, tokenDir);

            Assert.True(File.Exists(credsPath));
            var tokenFile = Path.Combine(tokenDir, SessionMaterializer.DriveTokenFileName);
            Assert.True(File.Exists(tokenFile));
            Assert.Contains("access_token", File.ReadAllText(tokenFile));

            // CaptureDriveToken reads that same token back.
            Assert.True(SessionMaterializer.CaptureDriveToken(tokenDir).TryGetProperty("access_token", out _));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
