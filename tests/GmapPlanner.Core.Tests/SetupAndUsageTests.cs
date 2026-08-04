using System.Text.Json.Nodes;
using GmapPlanner.Core;
using GmapPlanner.Core.Services;
using Xunit;

namespace GmapPlanner.Core.Tests;

public class SetupBundleServiceTests
{
    [Fact]
    public void Apply_FillsKeysAndWritesCredentialsJson()
    {
        WithTempDataDir(dir =>
        {
            var bundle = new JsonObject
            {
                ["GOOGLE_API_KEY"] = "gem",
                ["GEO_API_KEY"] = "geo",
                ["GCP_SA_JSON"] = new JsonObject { ["project_id"] = "proj-1" },
                ["credentials"] = new JsonObject { ["installed"] = new JsonObject { ["client_id"] = "abc" } },
            };

            var result = SetupBundleService.Apply(bundle);

            Assert.Contains("GOOGLE_API_KEY", result.Applied);
            Assert.Contains("GEO_API_KEY", result.Applied);
            Assert.Contains("GCP_SA_JSON", result.Applied);
            Assert.Contains("credentials.json", result.Applied);

            var saved = AppSettingsService.Load();
            Assert.Equal("gem", saved.GoogleApiKey);
            Assert.Equal("geo", saved.GeoApiKey);
            Assert.Contains("proj-1", saved.GcpSaJson); // SA object re-serialized to text

            Assert.True(File.Exists(AppConfig.DriveCredentialsFile));
            Assert.Contains("abc", File.ReadAllText(AppConfig.DriveCredentialsFile));
        });
    }

    [Fact]
    public void Apply_MissingAndEmptyKeysKeepCurrentValues()
    {
        WithTempDataDir(dir =>
        {
            AppSettingsService.Save(new AppSettings { GoogleApiKey = "keep", GeoApiKey = "keepgeo" });

            // GOOGLE_API_KEY absent; GEO_API_KEY present but blank → both keep prior value.
            var result = SetupBundleService.Apply(new JsonObject { ["GEO_API_KEY"] = "   " });

            Assert.False(result.AnythingApplied);
            var saved = AppSettingsService.Load();
            Assert.Equal("keep", saved.GoogleApiKey);
            Assert.Equal("keepgeo", saved.GeoApiKey);
        });
    }

    [Fact]
    public void Apply_InvalidCredentialsJson_IsSkippedNotWritten()
    {
        WithTempDataDir(dir =>
        {
            // `credentials` as a bare string that isn't JSON — must not create the file.
            var result = SetupBundleService.Apply(new JsonObject
            {
                ["GEO_API_KEY"] = "geo",
                ["credentials"] = "not json at all",
            });

            Assert.Contains("GEO_API_KEY", result.Applied);
            Assert.DoesNotContain("credentials.json", result.Applied);
            Assert.False(File.Exists(AppConfig.DriveCredentialsFile));
        });
    }

    [Fact]
    public void ApplyFromText_NonObjectJson_Throws()
    {
        WithTempDataDir(_ =>
            Assert.Throws<ArgumentException>(() => SetupBundleService.ApplyFromText("[1,2,3]")));
    }

    private static void WithTempDataDir(Action<string> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), "gmap-setup-" + Guid.NewGuid());
        var prev = Environment.GetEnvironmentVariable("TRIP_MAP_DATA_DIR");
        Environment.SetEnvironmentVariable("TRIP_MAP_DATA_DIR", dir);
        try { body(dir); }
        finally
        {
            Environment.SetEnvironmentVariable("TRIP_MAP_DATA_DIR", prev);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}

public class UsageRingTests
{
    [Fact]
    public void ArcGeometry_ZeroPercent_IsEmpty() =>
        Assert.Equal("", UsageRing.ArcGeometry(0));

    [Fact]
    public void ArcGeometry_StartsAtTwelveOClock()
    {
        // Every non-empty arc begins at the top of the circle (cx, cy-r) = (70, 16).
        Assert.StartsWith("M 70,16", UsageRing.ArcGeometry(25));
        Assert.Contains("A 54,54", UsageRing.ArcGeometry(25));
    }

    [Theory]
    [InlineData(25, 0)]   // quarter turn (90°) — small arc
    [InlineData(50, 0)]   // half turn (180°) — boundary, still small-arc flag
    [InlineData(75, 1)]   // three-quarters (270°) — large-arc flag set
    [InlineData(100, 1)]  // full ring (capped) — large-arc flag set
    public void ArcGeometry_SetsLargeArcFlagPastHalf(double pct, int expectedLargeArc)
    {
        // "M x,y A rx,ry rotation largeArc sweep endX,endY"
        var flag = UsageRing.ArcGeometry(pct).Split(' ')[5];
        Assert.Equal(expectedLargeArc.ToString(), flag);
    }

    [Theory]
    [InlineData(0, "#388E3C")]
    [InlineData(69.9, "#388E3C")]
    [InlineData(70, "#E65100")]
    [InlineData(89.9, "#E65100")]
    [InlineData(90, "#D32F2F")]
    [InlineData(100, "#D32F2F")]
    public void GaugeColor_ShiftsAtSeventyAndNinety(double pct, string expected) =>
        Assert.Equal(expected, UsageRing.GaugeColor(pct));
}

public class UsageServiceTests
{
    [Theory]
    [InlineData("""{"project_id":"my-proj","type":"service_account"}""", "my-proj")]
    [InlineData("""{"type":"service_account"}""", null)]
    [InlineData("not json", null)]
    [InlineData("", null)]
    public void ProjectIdOf_ReadsProjectFromServiceAccountJson(string json, string? expected) =>
        Assert.Equal(expected, UsageService.ProjectIdOf(json));
}
