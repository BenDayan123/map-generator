namespace GmapPlanner.Core.Models;

public record Location
{
    public required string Name { get; set; }
    public double Lat { get; set; }
    public double Lng { get; set; }
    public string Notes { get; set; } = "";
}
