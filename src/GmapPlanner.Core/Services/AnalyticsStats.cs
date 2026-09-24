using System.Globalization;

namespace GmapPlanner.Core.Services;

/// <summary>The Analytics page's time filter.</summary>
public enum AnalyticsRange { AllTime, Last30Days }

public sealed record AnalyticsSummary(int Trips, int Maps, int Places, double AvgPlaces);

/// <summary>One column of the over-time chart: a month (all time) or a day (last 30 days).</summary>
public sealed record AnalyticsBucket(DateTime Start, int Trips, int Maps, int Places);

/// <summary>
/// Pure number-crunching over the analytics Sheet rows, shared by every host. Each Sheet row is
/// one generated trip; <c>CreatedAt</c> comes back as the Sheet's formatted "yyyy-mm-dd hh:mm".
/// </summary>
public static class AnalyticsStats
{
    /// <summary>All time charts at most this many months, ending with the current one.</summary>
    public const int MaxMonths = 12;

    public static DateTime? ParseDate(string createdAt) =>
        DateTime.TryParse(createdAt, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d) ? d : null;

    /// <summary>The rows inside the range (rows with an unreadable date only count for all time).</summary>
    public static List<AnalyticsRow> Filter(IEnumerable<AnalyticsRow> rows, AnalyticsRange range, DateTime now)
    {
        if (range == AnalyticsRange.AllTime) return rows.ToList();
        var from = now.Date.AddDays(-29);
        return rows.Where(r => ParseDate(r.CreatedAt) is { } d && d >= from && d < now.Date.AddDays(1)).ToList();
    }

    public static AnalyticsSummary Summarize(IReadOnlyCollection<AnalyticsRow> rows)
    {
        var places = rows.Sum(r => r.Places);
        return new(rows.Count, rows.Sum(r => r.Maps), places, rows.Count == 0 ? 0 : (double)places / rows.Count);
    }

    /// <summary>
    /// Buckets for the over-time chart, oldest first, with empty buckets kept so gaps show:
    /// 30 days for <see cref="AnalyticsRange.Last30Days"/>, else months from the first trip
    /// (at most <see cref="MaxMonths"/>) through the current month.
    /// </summary>
    public static List<AnalyticsBucket> Timeline(IReadOnlyCollection<AnalyticsRow> rows, AnalyticsRange range, DateTime now)
    {
        var dated = rows.Select(r => (Row: r, Date: ParseDate(r.CreatedAt))).Where(x => x.Date is not null).ToList();
        var starts = new List<DateTime>();
        Func<DateTime, DateTime> key;
        if (range == AnalyticsRange.Last30Days)
        {
            for (var i = 29; i >= 0; i--) starts.Add(now.Date.AddDays(-i));
            key = d => d.Date;
        }
        else
        {
            var thisMonth = new DateTime(now.Year, now.Month, 1);
            var first = dated.Count == 0 ? thisMonth : dated.Min(x => x.Date!.Value);
            var start = new DateTime(first.Year, first.Month, 1);
            if (start < thisMonth.AddMonths(-(MaxMonths - 1))) start = thisMonth.AddMonths(-(MaxMonths - 1));
            for (var m = start; m <= thisMonth; m = m.AddMonths(1)) starts.Add(m);
            key = d => new DateTime(d.Year, d.Month, 1);
        }

        var groups = dated.GroupBy(x => key(x.Date!.Value)).ToDictionary(g => g.Key, g => g.Select(x => x.Row).ToList());
        return starts.Select(s => groups.TryGetValue(s, out var g)
            ? new AnalyticsBucket(s, g.Count, g.Sum(r => r.Maps), g.Sum(r => r.Places))
            : new AnalyticsBucket(s, 0, 0, 0)).ToList();
    }

    /// <summary>The <paramref name="count"/> trips with the most places, biggest first.</summary>
    public static List<AnalyticsRow> Biggest(IEnumerable<AnalyticsRow> rows, int count) =>
        rows.OrderByDescending(r => r.Places).ThenByDescending(r => ParseDate(r.CreatedAt)).Take(count).ToList();

    /// <summary>The <paramref name="count"/> newest trips (Sheet order is append order, so newest last).</summary>
    public static List<AnalyticsRow> Recent(IReadOnlyList<AnalyticsRow> rows, int count) =>
        rows.Reverse().Take(count).ToList();
}
