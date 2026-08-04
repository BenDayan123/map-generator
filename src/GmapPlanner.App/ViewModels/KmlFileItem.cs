namespace GmapPlanner.App.ViewModels;

/// <summary>One generated KML file, as shown in the results list.</summary>
public record KmlFileItem(string DayLabel, string FileName, string SizeText, string Path)
{
    public static KmlFileItem FromPath(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        var label = System.IO.Path.GetFileNameWithoutExtension(path);
        var dayLabel = label.Contains('-') ? $"Days {label}" : $"Day {label}";
        var sizeKb = new FileInfo(path).Length / 1024.0;
        return new KmlFileItem(dayLabel, name, $"{sizeKb:F0} KB", path);
    }
}
