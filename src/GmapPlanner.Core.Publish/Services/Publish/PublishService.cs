namespace GmapPlanner.Core.Services.Publish;

public record PublishedMap
{
    public required string File { get; init; }
    public required string Title { get; init; }
    public string Url { get; set; } = "";
    public string Mid { get; set; } = "";
    public List<string> SharedWith { get; set; } = [];
    public string Error { get; set; } = "";

    /// <summary>The public viewer link, which is what recipients should open.</summary>
    public string ViewUrl => string.IsNullOrEmpty(Mid)
        ? Url
        : $"https://www.google.com/maps/d/viewer?mid={Mid}";
}

/// <summary>
/// Ports gmap_planner/publish.py: KML files → My Maps maps → shared with people.
/// One KML file becomes one My Maps map.
/// </summary>
public static class PublishService
{
    /// <summary>Map title: '&lt;trip&gt; — Day(s) N', derived from the KML filename (e.g. 1-10.kml).</summary>
    public static string TitleFor(string kmlPath, string tripName)
    {
        var stem = Path.GetFileNameWithoutExtension(kmlPath);
        var span = stem.Contains('-') ? $"Days {stem}" : $"Day {stem}";
        return string.IsNullOrEmpty(tripName) ? span : $"{tripName} — {span}";
    }

    /// <summary>
    /// Creates one shared My Maps map per KML file. Never throws for a per-file failure —
    /// those are captured on <see cref="PublishedMap.Error"/> so one bad file can't abort
    /// the rest of the batch.
    /// </summary>
    public static async Task<List<PublishedMap>> PublishKmlFilesAsync(
        IReadOnlyList<string> kmlFiles,
        string tripName,
        IReadOnlyList<string> recipients,
        string role = "reader",
        bool headless = true,
        bool notify = true,
        ProgressCallback? progress = null,
        Action<string>? log = null,
        CancellationToken ct = default)
    {
        // Authenticate Drive up front (one consent) so we fail fast before the browser.
        // Every map gets its download/copy restriction applied, so Drive is useful even
        // with no recipients — but without them it stays optional (no credentials needed).
        DriveShareService? drive = null;
        try
        {
            drive = await DriveShareService.CreateAsync(ct: ct);
        }
        catch (DriveShareException)
        {
            if (recipients.Count > 0) throw;
        }

        await using var session = await MyMapsSession.StartAsync(headless: headless, log: log);
        return await PublishWithAsync(session, drive, kmlFiles, tripName, recipients, role, notify, progress, ct);
    }

    /// <summary>
    /// The per-KML create → restrict → share loop over an already-open session and (optional) Drive
    /// service. The desktop path builds those from the persistent profile / file OAuth; the cloud
    /// worker passes a storage-state session and a Drive service built from the user's session.json.
    /// </summary>
    public static async Task<List<PublishedMap>> PublishWithAsync(
        MyMapsSession session,
        DriveShareService? drive,
        IReadOnlyList<string> kmlFiles,
        string tripName,
        IReadOnlyList<string> recipients,
        string role = "reader",
        bool notify = true,
        ProgressCallback? progress = null,
        CancellationToken ct = default)
    {
        var results = new List<PublishedMap>();
        var total = kmlFiles.Count;

        for (var i = 0; i < total; i++)
        {
            ct.ThrowIfCancellationRequested();
            var kml = kmlFiles[i];
            var title = TitleFor(kml, tripName);
            var record = new PublishedMap { File = kml, Title = title };
            progress?.Invoke($"Creating map {i + 1}/{total}: {title}", (i + 0.3) / total);
            try
            {
                var created = await session.CreateMapFromKmlAsync(kml, title, ct);
                record.Url = created.Url;
                record.Mid = created.Mid;

                if (drive is not null)
                    await drive.RestrictDownloadAsync(record.Mid, title, ct);

                if (recipients.Count > 0)
                {
                    progress?.Invoke($"Sharing map {i + 1}/{total}", (i + 0.7) / total);
                    record.SharedWith = await drive!.ShareMapAsync(
                        record.Mid, recipients, role, title, notify, ct);
                }
            }
            catch (Exception e)
            {
                record.Error = e.Message; // capture, keep going
            }
            results.Add(record);
        }

        progress?.Invoke("Done", 1.0);
        return results;
    }
}
