using Google.Apis.Auth.OAuth2;
using Google.Apis.Drive.v3;
using Google.Apis.Services;
using Google.Apis.Util.Store;
using GmapPlanner.Core.Errors;

namespace GmapPlanner.Core.Services.Publish;

/// <summary>Drive sharing failed (auth, missing credentials, API error).</summary>
public class DriveShareException : PipelineException
{
    public DriveShareException(string message) : base(message) { }
    public DriveShareException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Ports gmap_planner/drive_share.py. A My Maps map is a Drive file
/// (mimeType application/vnd.google-apps.map), so once the browser step gives us the
/// map's id (mid), Drive's permissions.create grants people access — far more reliable
/// than driving the share dialog in the UI.
/// </summary>
public class DriveShareService(DriveService drive)
{
    /// <summary>
    /// Builds an authenticated Drive v3 service, running the OAuth consent if needed.
    /// Uses the standard installed-app flow: a credentials.json OAuth client plus a
    /// cached token, both under the app data dir.
    /// </summary>
    public static async Task<DriveShareService> CreateAsync(
        string? credentialsPath = null,
        string? tokenDir = null,
        CancellationToken ct = default)
    {
        credentialsPath ??= AppConfig.DriveCredentialsFile;
        tokenDir ??= AppConfig.DriveTokenDir;

        if (!File.Exists(credentialsPath))
        {
            throw new DriveShareException(
                $"Drive OAuth client '{credentialsPath}' not found. Create an OAuth 2.0 " +
                "Desktop client in Google Cloud Console, enable the Drive API, and save " +
                "it there as credentials.json.");
        }

        UserCredential credential;
        try
        {
            await using var stream = File.OpenRead(credentialsPath);
            credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                GoogleClientSecrets.FromStream(stream).Secrets,
                AppConfig.DriveScopes,
                "user",
                ct,
                new FileDataStore(tokenDir, fullPath: true));
        }
        catch (Exception e)
        {
            throw new DriveShareException($"Google Drive sign-in failed: {e.Message}", e);
        }

        var service = new DriveService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "Trip Map Maker",
        });
        return new DriveShareService(service);
    }

    /// <summary>Maps a friendly role (viewer/editor/...) to a Drive permission role.</summary>
    public static string NormalizeRole(string? role)
    {
        var key = (role ?? "reader").Trim();
        if (key.Length == 0) key = "reader";
        if (!AppConfig.DriveRoleAliases.TryGetValue(key, out var drive))
        {
            var allowed = string.Join(", ", AppConfig.DriveRoleAliases.Keys.Order());
            throw new DriveShareException($"Unknown share role '{role}'. Use one of: {allowed}.");
        }
        return drive;
    }

    /// <summary>Confirms mid is a reachable Drive map; falls back to a title search.</summary>
    private async Task<string> ResolveFileIdAsync(string mid, string? title, CancellationToken ct)
    {
        try
        {
            var get = drive.Files.Get(mid);
            get.Fields = "id";
            await get.ExecuteAsync(ct);
            return mid;
        }
        catch
        {
            // Fall through to the title search below.
        }

        if (!string.IsNullOrEmpty(title))
        {
            var list = drive.Files.List();
            // Escape single quotes so a trip name containing one can't break the query.
            var safeTitle = title.Replace("'", @"\'");
            list.Q = $"mimeType='{AppConfig.MyMapsMapMime}' and name='{safeTitle}' and trashed=false";
            list.OrderBy = "modifiedTime desc";
            list.PageSize = 1;
            list.Fields = "files(id)";
            var response = await list.ExecuteAsync(ct);
            var found = response.Files?.FirstOrDefault();
            if (found is not null) return found.Id;
        }

        throw new DriveShareException($"Could not locate the map in Drive (mid={mid}, title='{title}').");
    }

    /// <summary>
    /// Turns off "Viewers and commenters can see the option to download, print, copy".
    /// Same switch as Share → gear → "Commenters and viewers" in the My Maps/Drive
    /// dialog; Drive exposes it as copyRequiresWriterPermission, so there's no dialog
    /// to drive. Best-effort: a failure here must not lose an otherwise-published map.
    /// </summary>
    public async Task<bool> RestrictDownloadAsync(string mid, string? title = null, CancellationToken ct = default)
    {
        try
        {
            var fileId = await ResolveFileIdAsync(mid, title, ct);
            var update = drive.Files.Update(
                new Google.Apis.Drive.v3.Data.File { CopyRequiresWriterPermission = true },
                fileId);
            update.Fields = "id";
            await update.ExecuteAsync(ct);
            return true;
        }
        catch (Exception e)
        {
            Console.WriteLine($"  ! Could not restrict download/copy for the map: {e.Message}");
            return false;
        }
    }

    /// <summary>Grants each email access to the map at the given role. Returns those shared with.</summary>
    public async Task<List<string>> ShareMapAsync(
        string mid,
        IEnumerable<string> emails,
        string role = "reader",
        string? title = null,
        bool notify = true,
        CancellationToken ct = default)
    {
        var recipients = emails.Select(e => e.Trim()).Where(e => e.Length > 0).ToList();
        if (recipients.Count == 0) return [];

        var driveRole = NormalizeRole(role);
        var fileId = await ResolveFileIdAsync(mid, title, ct);

        var shared = new List<string>();
        foreach (var email in recipients)
        {
            try
            {
                var create = drive.Permissions.Create(
                    new Google.Apis.Drive.v3.Data.Permission
                    {
                        Type = "user",
                        Role = driveRole,
                        EmailAddress = email,
                    },
                    fileId);
                create.SendNotificationEmail = notify;
                create.Fields = "id";
                await create.ExecuteAsync(ct);
                shared.Add(email);
            }
            catch (Exception e)
            {
                throw new DriveShareException($"Failed to share map with {email}: {e.Message}", e);
            }
        }
        return shared;
    }
}
