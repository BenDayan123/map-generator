using GmapPlanner.Core.Services;

namespace GmapPlanner.Core.Tests;

public class AnalyticsStatsTests
{
    private static readonly DateTime Now = new(2026, 9, 24, 12, 0, 0);

    private static AnalyticsRow Row(string at, int maps, int places, string name = "t") => new(at, name, maps, places, []);

    private static readonly AnalyticsRow[] Rows =
    [
        Row("2025-06-02 10:00", 1, 10), // outside the 12-month window
        Row("2026-07-15 09:30", 2, 30),
        Row("2026-09-01 08:00", 1, 5),  // 23 days ago
        Row("2026-09-24 11:00", 3, 40), // today
        Row("not a date", 1, 7),
    ];

    [Fact]
    public void Filter_last30Days_keepsOnlyDatedRowsInTheWindow()
    {
        var last30 = AnalyticsStats.Filter(Rows, AnalyticsRange.Last30Days, Now);
        Assert.Equal([5, 40], last30.Select(r => r.Places));
        Assert.Equal(5, AnalyticsStats.Filter(Rows, AnalyticsRange.AllTime, Now).Count);
    }

    [Fact]
    public void Summarize_totalsAndAverage()
    {
        var s = AnalyticsStats.Summarize(Rows);
        Assert.Equal(new AnalyticsSummary(5, 8, 92, 18.4), s);
        Assert.Equal(0, AnalyticsStats.Summarize([]).AvgPlaces);
    }

    [Fact]
    public void Timeline_last30Days_isThirtyDailyBucketsEndingToday()
    {
        var t = AnalyticsStats.Timeline(Rows, AnalyticsRange.Last30Days, Now);
        Assert.Equal(30, t.Count);
        Assert.Equal(Now.Date, t[^1].Start);
        Assert.Equal(40, t[^1].Places);
        Assert.Equal(5, t.Single(b => b.Start == new DateTime(2026, 9, 1)).Places);
    }

    [Fact]
    public void Timeline_allTime_isMonthlyCappedAtTwelveWithGapsKept()
    {
        var t = AnalyticsStats.Timeline(Rows, AnalyticsRange.AllTime, Now);
        Assert.Equal(AnalyticsStats.MaxMonths, t.Count);
        Assert.Equal(new DateTime(2025, 10, 1), t[0].Start);
        Assert.Equal(new DateTime(2026, 9, 1), t[^1].Start);
        Assert.Equal(0, t.Single(b => b.Start == new DateTime(2026, 8, 1)).Trips); // empty month stays
        Assert.Equal((2, 45), (t[^1].Trips, t[^1].Places));
    }

    [Fact]
    public void Biggest_and_Recent()
    {
        Assert.Equal([40, 30], AnalyticsStats.Biggest(Rows, 2).Select(r => r.Places));
        Assert.Equal(["not a date", "2026-09-24 11:00"], AnalyticsStats.Recent(Rows, 2).Select(r => r.CreatedAt));
    }
}
