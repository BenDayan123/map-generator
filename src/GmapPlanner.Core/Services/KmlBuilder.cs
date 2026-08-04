using System.Text;
using System.Xml;
using System.Xml.Linq;
using GmapPlanner.Core.Models;
using SharpKml.Base;
using SharpKml.Dom;

namespace GmapPlanner.Core.Services;

/// <summary>Ports gmap_planner/kml.py: chunks days into layers and writes KML files.</summary>
public static class KmlBuilder
{
    // Characters not allowed in Windows file/folder names.
    private static readonly HashSet<char> InvalidNameChars = BuildInvalidNameChars();

    private static HashSet<char> BuildInvalidNameChars()
    {
        var set = new HashSet<char>("<>:\"/\\|?*");
        for (var c = '\x00'; c <= '\x1f'; c++) set.Add(c);
        return set;
    }

    /// <summary>Turns a trip name into a safe folder name (cross-platform).</summary>
    public static string SanitizeFolderName(string? name)
    {
        var cleaned = new string((name ?? "").Trim().Where(c => !InvalidNameChars.Contains(c)).ToArray())
            .TrimEnd('.', ' ');
        return string.IsNullOrEmpty(cleaned) ? "Trip" : cleaned;
    }

    /// <summary>
    /// Google My Maps icon URL: a solid-color teardrop pin with <paramref name="n"/> in
    /// solid white. 3-layer stack so the digit fills solid instead of a hollow outline.
    /// <paramref name="color"/> is hex RGB (no #); the number is always white for contrast.
    /// </summary>
    public static string NumberedPinHref(int n, string color = "0288D1")
    {
        var psize = n < 10 ? 20 : n < 100 ? 17 : 12;
        return "https://mt.google.com/vt/icon/name="
            + "icons/onion/SHARED-mymaps-pin-container_4x.png,"
            + "icons/onion/SHARED-mymaps-container_4x.png,"
            + "icons/onion/1899-blank-shape_pin_4x.png"
            + $"&highlight={color},{color},ffffff&scale=4.0&color=ffffffff"
            + $"&font=fonts/Roboto-Regular.ttf&ay=46&psize={psize}&text={n}";
    }

    /// <summary>Splits days into chunks of at most `layersPerFile` (one day = one layer).</summary>
    public static List<List<Day>> ChunkDays(List<Day> days, int layersPerFile)
    {
        var size = Math.Max(1, Math.Min(layersPerFile, AppConfig.MaxLayersPerFile));
        var chunks = new List<List<Day>>();
        for (var i = 0; i < days.Count; i += size)
            chunks.Add(days.Skip(i).Take(size).ToList());
        return chunks;
    }

    /// <summary>Builds one KML document where each day is its own Folder (= one My Maps layer).</summary>
    public static Kml BuildKmlFile(List<Day> daysSlice)
    {
        var document = new Document();
        var dayNumbers = daysSlice.Select(d => d.DayNumber).ToList();
        document.Name = dayNumbers.Count > 1
            ? $"Days {dayNumbers[0]}-{dayNumbers[^1]}"
            : $"Day {dayNumbers[0]}";

        foreach (var day in daysSlice)
        {
            var color = AppConfig.DayColors[(day.DayNumber - 1) % AppConfig.DayColors.Length];
            var folder = new Folder
            {
                Name = $"Day {day.DayNumber}" + (string.IsNullOrEmpty(day.Date) ? "" : $" ({day.Date})"),
            };

            var idx = 0;
            foreach (var loc in day.Locations)
            {
                idx++;
                var placemark = new Placemark
                {
                    Name = loc.Name,
                    Description = new Description { Text = loc.Notes },
                    Geometry = new Point { Coordinate = new Vector(loc.Lat, loc.Lng, 0) },
                };
                placemark.AddStyle(new Style
                {
                    Icon = new IconStyle
                    {
                        Icon = new IconStyle.IconLink(new Uri(NumberedPinHref(idx, color))),
                    },
                    Label = new LabelStyle { Scale = 0.8 },
                });
                folder.AddFeature(placemark);
            }
            document.AddFeature(folder);
        }

        return new Kml { Feature = document };
    }

    /// <summary>Writes each chunk to `{first}.kml` or `{first}-{last}.kml` under outputDir.</summary>
    public static List<string> WriteKmlFiles(List<List<Day>> chunks, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var paths = new List<string>();
        foreach (var chunk in chunks)
        {
            var kml = BuildKmlFile(chunk);
            var serializer = new Serializer();
            serializer.Serialize(kml);

            var first = chunk[0].DayNumber;
            var last = chunk[^1].DayNumber;
            var filename = first == last ? $"{first}.kml" : $"{first}-{last}.kml";
            var path = Path.Combine(outputDir, filename);

            var xdoc = XDocument.Parse(serializer.Xml);
            using (var writer = XmlWriter.Create(path, new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "  ",
                Encoding = new UTF8Encoding(false),
            }))
            {
                xdoc.Save(writer);
            }
            paths.Add(path);
        }
        return paths;
    }
}
