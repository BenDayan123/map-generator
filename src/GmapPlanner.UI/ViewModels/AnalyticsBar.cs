namespace GmapPlanner.App.ViewModels;

// Analytics page chart items. Sizes are computed in the VM so the view needs no converters.

/// <summary>One column of the "places over time" chart.</summary>
public sealed class AnalyticsColumn
{
    public required string Label { get; init; }   // "" for columns that skip their axis label
    public required double BarHeight { get; init; }
    public required string Tip { get; init; }      // hover tooltip: period + trips / maps / places
}

/// <summary>One row of the "biggest trips" bars; <see cref="Fraction"/> is 0..1 of the largest.</summary>
public sealed class AnalyticsTopTrip
{
    public required string Name { get; init; }
    public required string DateText { get; init; }
    public required int Places { get; init; }
    public required double Fraction { get; init; }
}

/// <summary>One row of the "recent trips" table.</summary>
public sealed class AnalyticsRecentTrip
{
    public required string Name { get; init; }
    public required string DateText { get; init; }
    public required int Maps { get; init; }
    public required int Places { get; init; }
    public required string Link { get; init; }
    public bool HasLink => Link.Length > 0;
}
