using System.Text.Json;
using GmapPlanner.Core.Json;
using GmapPlanner.Core.Services;
using GmapPlanner.Core.Services.Publish;
using Xunit;

namespace GmapPlanner.Core.Tests;

public class SessionAndJobParsingTests
{
    [Fact]
    public void SessionFile_parses_v1()
    {
        var json = """{"version":1,"storageState":{"cookies":[]},"driveCredentials":{"installed":{}},"driveToken":{"access_token":"x"}}""";
        var s = JsonSerializer.Deserialize(json, PublishJsonContext.Default.SessionFile);
        Assert.Equal(1, s!.Version);
        Assert.True(s.StorageState.TryGetProperty("cookies", out _));
        Assert.True(s.DriveToken.TryGetProperty("access_token", out _));
    }

    [Fact]
    public void JobPayload_round_trips_through_source_gen()
    {
        var payload = new JobPayload(
            TripName: "Kyoto",
            Kmls: [new KmlFile("day1.kml", "<kml/>")],
            Recipients: ["a@x.com"],
            Role: "reader",
            Notify: true,
            Session: new SessionFile(1, Empty("{}"), Empty("{}"), Empty("{}")),
            SaJson: null,
            SheetId: "abc");

        var json = JsonSerializer.Serialize(payload, PublishJsonContext.Default.JobPayload);
        // camelCase policy is on the wire; the browser and worker both use this context.
        Assert.Contains("\"tripName\":\"Kyoto\"", json);
        Assert.Contains("\"kmls\":", json);

        var back = JsonSerializer.Deserialize(json, PublishJsonContext.Default.JobPayload)!;
        Assert.Equal("Kyoto", back.TripName);
        Assert.Single(back.Kmls);
        Assert.Equal("day1.kml", back.Kmls[0].FileName);
        Assert.Equal("abc", back.SheetId);
        Assert.Null(back.SaJson);
    }

    [Fact]
    public void JobStatus_carries_maps_and_error_code()
    {
        var status = new JobStatus("failed", Message: "map 2/3", ErrorCode: "SESSION_EXPIRED");
        var json = JsonSerializer.Serialize(status, PublishJsonContext.Default.JobStatus);
        var back = JsonSerializer.Deserialize(json, PublishJsonContext.Default.JobStatus)!;
        Assert.Equal("failed", back.State);
        Assert.Equal("SESSION_EXPIRED", back.ErrorCode);
        Assert.Null(back.Maps);
    }

    private static JsonElement Empty(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
