using GmapPlanner.Worker;
using Xunit;

namespace GmapPlanner.Core.Tests;

/// <summary>
/// Pins the worker's HMAC to the exact bytes web/api/_lib/crypto.ts produces
/// (createHmac('sha256', secret).update(body).digest('hex')). Vectors were generated with node;
/// if this drifts, worker claim/status calls will 401.
/// </summary>
public class WorkerHmacTests
{
    [Theory]
    [InlineData("body", "s3cret", "63d7d468988fe3de60e994cf90f219719ea4b63df0ee7521079ed251fa76e83d")]
    [InlineData("", "WORKER_SECRET", "48604f4210a0641a8d9d027ad3854d0919bbabf2c33d542996d2d81e2d13f5a2")]
    public void Hmac_matches_the_typescript_vector(string body, string secret, string expected) =>
        Assert.Equal(expected, WorkerApi.Hmac(body, secret));
}
