namespace GmapPlanner.Core.Services;

/// <summary>One API's usage as a percent-of-quota gauge.</summary>
public record UsageGauge(int Used, int Limit, double Percent, int? ResetDays);
