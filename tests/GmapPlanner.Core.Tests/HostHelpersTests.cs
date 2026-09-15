using System.Text.Json.Nodes;
using GmapPlanner.Core.Services;
using Xunit;

namespace GmapPlanner.Core.Tests;

/// <summary>Pure helpers both hosts share (the browser has no config.json and no credentials file).</summary>
public class HostHelpersTests
{
    [Fact]
    public void SettingsJson_RoundTrips()
    {
        var json = AppSettingsService.ToJson(new AppSettings { GoogleApiKey = "g", GeoApiKey = "p", AnalyticsSheetId = "s" });

        var loaded = AppSettingsService.FromJson(json);

        Assert.Equal("g", loaded.GoogleApiKey);
        Assert.Equal("p", loaded.GeoApiKey);
        Assert.Equal("s", loaded.AnalyticsSheetId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    public void SettingsJson_BadInputGivesDefaults(string? json) =>
        Assert.Equal("", AppSettingsService.FromJson(json).GoogleApiKey);

    [Fact]
    public void Merge_FillsPresentKeys_KeepsOthers_AndNeverMutatesCurrent()
    {
        var current = new AppSettings { GoogleApiKey = "old-gemini", GeoApiKey = "old-geo" };
        var bundle = new JsonObject
        {
            ["GOOGLE_API_KEY"] = "new-gemini",
            ["GEO_API_KEY"] = "  ",
            ["credentials"] = new JsonObject { ["installed"] = new JsonObject { ["client_id"] = "x" } },
        };

        var merged = SetupBundleService.Merge(bundle, current);

        Assert.Equal("new-gemini", merged.Settings.GoogleApiKey);
        Assert.Equal("old-geo", merged.Settings.GeoApiKey);
        Assert.Equal("old-gemini", current.GoogleApiKey);
        Assert.NotNull(merged.CredentialsJson);
        Assert.Equal("x", JsonNode.Parse(merged.CredentialsJson!)!["installed"]!["client_id"]!.GetValue<string>());
        Assert.Equal(new[] { "GOOGLE_API_KEY", "credentials.json" }, merged.Applied);
    }

    [Fact]
    public void Merge_SkipsCredentialsThatAreNotJson()
    {
        var merged = SetupBundleService.Merge(new JsonObject { ["credentials"] = "not json" }, new AppSettings());

        Assert.Null(merged.CredentialsJson);
        Assert.Empty(merged.Applied);
    }

    [Fact]
    public void MergeFromText_RejectsNonObjects() =>
        Assert.Throws<ArgumentException>(() => SetupBundleService.MergeFromText("[1,2,3]", new AppSettings()));
}
