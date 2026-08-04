namespace GmapPlanner.Core.Models;

public record Day
{
    public int DayNumber { get; set; }

    /// <summary>DD/MM, or empty string if undetermined.</summary>
    public string Date { get; set; } = "";

    public List<Location> Locations { get; set; } = [];
}
