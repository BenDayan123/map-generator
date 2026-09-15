using System.Text.Json;
using GmapPlanner.Core.Json;
using Xunit;

namespace GmapPlanner.Core.Tests;

public class BrowserApiContractTests
{
    [Fact]
    public void UsageResponse_roundtrips_through_source_gen_context()
    {
        var json = """{"used":1234,"limit":5000,"percent":25,"resetDays":9}""";
        var dto = JsonSerializer.Deserialize(json, GmapPlannerJsonContext.Default.UsageApiResponse);
        Assert.NotNull(dto);
        Assert.Equal(1234, dto!.Used);
        Assert.Equal(5000, dto.Limit);
        Assert.Equal(25d, dto.Percent);
        Assert.Equal(9, dto.ResetDays);
    }

    [Fact]
    public void AnalyticsResponse_roundtrips_and_maps_rows()
    {
        var json = """{"rows":[{"createdAt":"2026-09-15 10:00","tripName":"Tokyo","maps":2,"places":11,"links":["a","b"]}]}""";
        var dto = JsonSerializer.Deserialize(json, GmapPlannerJsonContext.Default.AnalyticsApiResponse);
        Assert.NotNull(dto);
        Assert.Single(dto!.Rows);
        Assert.Equal("Tokyo", dto.Rows[0].TripName);
        Assert.Equal(11, dto.Rows[0].Places);
        Assert.Equal(2, dto.Rows[0].Links.Count);
    }
}
