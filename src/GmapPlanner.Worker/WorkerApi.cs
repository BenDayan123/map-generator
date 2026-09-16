using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using GmapPlanner.Core.Json;
using GmapPlanner.Core.Services.Publish;

namespace GmapPlanner.Worker;

/// <summary>
/// Claim/report calls to the jobs API, authenticated with HMAC-SHA256 (lowercase hex) over the
/// exact raw request body — the same contract as web/api/_lib/crypto.ts (createHmac('sha256').digest('hex')).
/// </summary>
public sealed class WorkerApi(string apiBase, string hmacSecret, string jobId)
{
    private readonly HttpClient _http = new();
    private readonly string _base = apiBase.TrimEnd('/');

    /// <summary>HMAC-SHA256 of <paramref name="body"/> under <paramref name="secret"/>, lowercase hex.</summary>
    internal static string Hmac(string body, string secret)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
    }

    /// <summary>Claims the job (single claim) and returns its decrypted payload.</summary>
    public async Task<JobPayload> ClaimAsync()
    {
        var json = await SignedPostAsync("claim", body: ""); // id is in the URL; empty body
        return JsonSerializer.Deserialize(json, PublishJsonContext.Default.JobPayload)
            ?? throw new InvalidOperationException("Claim returned an empty payload.");
    }

    /// <summary>Posts a progress/terminal status back to the API.</summary>
    public async Task PostStatusAsync(JobStatus status)
    {
        var body = JsonSerializer.Serialize(status, PublishJsonContext.Default.JobStatus);
        await SignedPostAsync("status", body);
    }

    private async Task<string> SignedPostAsync(string action, string body)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, $"{_base}/api/jobs/{jobId}/{action}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        req.Headers.Add("x-worker-signature", Hmac(body, hmacSecret));
        var res = await _http.SendAsync(req);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadAsStringAsync();
    }
}
