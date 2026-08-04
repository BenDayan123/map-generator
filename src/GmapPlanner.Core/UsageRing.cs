using System.Globalization;

namespace GmapPlanner.Core;

/// <summary>
/// Geometry + color for the circular usage gauge (ports streamlit_app.py's ring_svg /
/// _gauge_color). Pure so the arc math is testable without a UI; the app wraps the path
/// string in an Avalonia Geometry.
/// </summary>
public static class UsageRing
{
    public const double Size = 140;
    public const double Center = 70;
    public const double Radius = 54;

    /// <summary>
    /// SVG-style path for a clockwise arc from 12 o'clock sweeping <paramref name="percent"/>
    /// of the circle. Empty string at 0%. Avalonia's Geometry.Parse accepts this syntax.
    /// </summary>
    public static string ArcGeometry(double percent)
    {
        var pct = Math.Clamp(percent, 0, 100);
        if (pct <= 0) return "";

        // Cap just under a full turn so 100% renders as a near-complete ring instead of a
        // zero-length degenerate arc (start point == end point).
        var sweep = Math.Min(pct / 100.0 * 360.0, 359.99);
        var theta = sweep * Math.PI / 180.0;

        var startX = Center;
        var startY = Center - Radius;
        var endX = Center + Radius * Math.Sin(theta);
        var endY = Center - Radius * Math.Cos(theta);
        var largeArc = sweep > 180 ? 1 : 0;

        // "M startX,startY A rx,ry rotation isLargeArc sweepDirection endX,endY" — sweep 1 = clockwise.
        return string.Format(CultureInfo.InvariantCulture,
            "M {0:0.###},{1:0.###} A {2},{2} 0 {3} 1 {4:0.###},{5:0.###}",
            startX, startY, Radius, largeArc, endX, endY);
    }

    /// <summary>Green under 70%, orange under 90%, red at/above 90% — matches _gauge_color.</summary>
    public static string GaugeColor(double percent) =>
        percent >= 90 ? "#D32F2F" : percent >= 70 ? "#E65100" : "#388E3C";
}
