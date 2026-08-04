namespace GmapPlanner.Core.Models;

public record Trip
{
    public required string TripName { get; set; }
    public List<Day> Days { get; set; } = [];
}
