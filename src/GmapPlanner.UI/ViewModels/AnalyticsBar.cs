namespace GmapPlanner.App.ViewModels;

/// <summary>One row of the analytics bar chart: a trip's place count, pre-scaled to pixels.</summary>
public sealed class AnalyticsBar
{
    public required string Label { get; init; }
    public required string ValueText { get; init; }

    /// <summary>Bar length in pixels (computed in the VM so the view needs no converter).</summary>
    public required double BarWidth { get; init; }
}
