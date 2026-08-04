using GmapPlanner.Core.Services;
using Xunit;

namespace GmapPlanner.Core.Tests;

public class AppSettingsServiceTests
{
    /// <summary>
    /// Regression: saving settings used to kill the app. Publishing trimmed disables
    /// reflection-based System.Text.Json, so JsonSerializer.Serialize(settings) threw
    /// InvalidOperationException — and picking an output folder writes settings, so the
    /// folder picker took the whole process down.
    /// </summary>
    [Fact]
    public void SaveThenLoad_RoundTripsWithoutReflectionBasedSerialization()
    {
        var dir = Path.Combine(Path.GetTempPath(), "gmap-planner-settings-" + Guid.NewGuid());
        var previous = Environment.GetEnvironmentVariable("TRIP_MAP_DATA_DIR");
        Environment.SetEnvironmentVariable("TRIP_MAP_DATA_DIR", dir);
        try
        {
            var settings = new AppSettings
            {
                GoogleApiKey = "gem-key",
                GeoApiKey = "geo-key",
                OutputDir = @"D:\Trips",
            };

            AppSettingsService.Save(settings);
            var loaded = AppSettingsService.Load();

            Assert.Equal("gem-key", loaded.GoogleApiKey);
            Assert.Equal("geo-key", loaded.GeoApiKey);
            Assert.Equal(@"D:\Trips", loaded.OutputDir);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TRIP_MAP_DATA_DIR", previous);
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
