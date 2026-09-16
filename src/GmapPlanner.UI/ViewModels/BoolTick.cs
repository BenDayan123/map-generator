using System.Globalization;
using Avalonia.Data.Converters;

namespace GmapPlanner.App.ViewModels;

/// <summary>true → ✓, false → ○. For the setup-status checklist. Plain glyphs (not emoji)
/// so they render in the browser host's bundled Inter font, which has no emoji.</summary>
public sealed class BoolTick : IValueConverter
{
    public static readonly BoolTick Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? "✓" : "○";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
