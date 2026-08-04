namespace GmapPlanner.Core;

/// <summary>Ports gmap_planner/paths.py: the writable per-user directory for app files.</summary>
public static class AppDataPaths
{
    private const string AppName = "GmapPlanner";

    public static string DataDir()
    {
        var overrideDir = Environment.GetEnvironmentVariable("TRIP_MAP_DATA_DIR");
        string baseDir;
        if (!string.IsNullOrEmpty(overrideDir))
        {
            baseDir = overrideDir;
        }
        else if (OperatingSystem.IsMacOS())
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            baseDir = Path.Combine(home, "Library", "Application Support", AppName);
        }
        else if (OperatingSystem.IsWindows())
        {
            baseDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
        }
        else
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            baseDir = xdg is { Length: > 0 } ? Path.Combine(xdg, AppName) : Path.Combine(home, ".local", "share", AppName);
        }
        Directory.CreateDirectory(baseDir);
        return baseDir;
    }

    public static string DataPath(string name) => Path.Combine(DataDir(), name);
}
