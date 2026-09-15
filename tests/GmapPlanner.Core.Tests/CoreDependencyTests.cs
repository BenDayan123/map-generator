using GmapPlanner.Core.Services;
using Xunit;

namespace GmapPlanner.Core.Tests;

/// <summary>
/// GmapPlanner.Core must stay loadable in the browser (Avalonia WASM), which can't run
/// Playwright or the Google.Apis auth stack. Those live in GmapPlanner.Core.Publish.
/// </summary>
public class CoreDependencyTests
{
    [Fact]
    public void Core_DoesNotReferencePlaywrightOrGoogleApis()
    {
        var referenced = typeof(KmlBuilder).Assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToList();

        Assert.DoesNotContain(referenced, n => n.StartsWith("Microsoft.Playwright", StringComparison.Ordinal));
        Assert.DoesNotContain(referenced, n => n.StartsWith("Google.Apis", StringComparison.Ordinal));
    }
}
