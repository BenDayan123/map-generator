# Cloud Hosting — Phase 1: Core/UI Split Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restructure the solution so a future Avalonia browser (WASM) host can reuse the generation pipeline and the UI, without changing desktop behaviour.

**Architecture:** Three structural moves, each leaving the desktop app working: (1) KML generation becomes in-memory (`KmlFile` records) with disk writing layered on top; (2) everything that references Playwright or Google.Apis moves from `GmapPlanner.Core` into a new `GmapPlanner.Core.Publish` library, so `Core` is browser-safe; (3) the view, view models and design system move from the desktop exe into a new `GmapPlanner.UI` Avalonia library — `MainWindow` becomes a thin desktop shell around a `MainView` `UserControl`.

**Tech Stack:** .NET 8, Avalonia 11.2.5, CommunityToolkit.Mvvm 8.4.2, SharpKml.Core 6.1.0, Microsoft.Playwright 1.61.0, Google.Apis.Drive.v3 1.75.0.4218, xunit 2.4.2.

**Spec:** `docs/superpowers/specs/2026-09-15-cloud-hosting-design.md` (Build order, phase 1). This is plan 1 of 5; phases 2–5 (browser host, Vercel API, login helper + worker, E2E) get their own plans once this lands.

## Global Constraints

- Branch: all work on `feature/cloud-hosting`. Never commit to `main`.
- Target framework `net8.0` for every project; Avalonia packages pinned at `11.2.5`.
- Desktop behaviour must not change: same UI, same publish, same release packaging.
- `.github/workflows/release.yml`, `build/installer.iss`, `build/make-macos-dmg.sh` stay untouched — the desktop exe project keeps its path `src/GmapPlanner.App` and assembly name `GmapPlanner.App`.
- **Deviation from spec (intentional):** the spec names the shared UI project `App` and the desktop host `App.Desktop`. This plan instead keeps the desktop host as `GmapPlanner.App` and names the shared project `GmapPlanner.UI`, so packaging paths don't move. Task 3 updates the spec to match.
- C# namespaces do **not** change when files move between projects (`GmapPlanner.Core.Services`, `GmapPlanner.Core.Services.Publish`, `GmapPlanner.App.ViewModels`, `GmapPlanner.App.Views`), so no `using` or `xmlns` churn.
- No DI container, no interface with a single implementation (CLAUDE.md).
- Trimming rule #1: no reflection-based `System.Text.Json`. Every serialized type goes through a source-generated `JsonSerializerContext`.
- Trimming rule #2: XAML keeps `x:CompileBindings="True"` with `x:DataType`.
- Every commit message ends with the line `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`.
- A running `GmapPlanner.App` locks its build output; close it before `dotnet build`.

## File Structure

| Path | Status | Responsibility |
|---|---|---|
| `src/GmapPlanner.Core/Services/KmlBuilder.cs` | modify | add `KmlFile` record, `BuildKmlFiles` (in-memory), `SaveKmlFiles` (disk) |
| `src/GmapPlanner.Core/Services/PipelineService.cs` | modify | add `GenerateAsync` (no disk); `RunAsync` = generate + save |
| `tests/GmapPlanner.Core.Tests/KmlBuilderTests.cs` | modify | in-memory KML tests |
| `tests/GmapPlanner.Core.Tests/PipelineServiceTests.cs` | create | `GenerateAsync` / `RunAsync` tests |
| `src/GmapPlanner.Core.Publish/GmapPlanner.Core.Publish.csproj` | create | library holding Playwright + Google.Apis dependencies |
| `src/GmapPlanner.Core.Publish/Services/Publish/*.cs` | move from Core | My Maps automation, Drive sharing, publish orchestration |
| `src/GmapPlanner.Core.Publish/Services/UsageService.cs` | move from Core | Cloud Monitoring usage gauge |
| `src/GmapPlanner.Core.Publish/Services/SheetsAnalyticsService.cs` | move from Core | analytics Sheet |
| `src/GmapPlanner.Core.Publish/Json/PublishJsonContext.cs` | create | source-gen JSON for `TimeSeriesResponse` |
| `src/GmapPlanner.Core/Json/GmapPlannerJsonContext.cs` | modify | drop `TimeSeriesResponse` |
| `src/GmapPlanner.Core/GmapPlanner.Core.csproj` | modify | drop Playwright + Drive packages |
| `tests/GmapPlanner.Core.Tests/CoreDependencyTests.cs` | create | guard: Core references neither Playwright nor Google.Apis |
| `src/GmapPlanner.UI/GmapPlanner.UI.csproj` | create | shared Avalonia library |
| `src/GmapPlanner.UI/Theme.axaml` | create | design tokens + styles (from `App.axaml`) |
| `src/GmapPlanner.UI/Views/MainView.axaml(.cs)` | create / move | the whole app UI as a `UserControl` |
| `src/GmapPlanner.UI/ViewModels/*.cs` | move from App | view models + converters |
| `src/GmapPlanner.App/App.axaml` | modify | host: `FluentTheme` + include `Theme.axaml` |
| `src/GmapPlanner.App/Views/MainWindow.axaml(.cs)` | rewrite | thin desktop window hosting `MainView` |
| `src/GmapPlanner.App/GmapPlanner.App.csproj` | modify | reference `GmapPlanner.UI` |
| `GmapPlanner.sln`, `CLAUDE.md`, spec | modify | register projects, document the new layout |

---

### Task 1: In-memory KML generation

**Files:**
- Modify: `src/GmapPlanner.Core/Services/KmlBuilder.cs:100-129`
- Modify: `src/GmapPlanner.Core/Services/PipelineService.cs`
- Modify: `tests/GmapPlanner.Core.Tests/KmlBuilderTests.cs`
- Create: `tests/GmapPlanner.Core.Tests/PipelineServiceTests.cs`

**Interfaces:**
- Consumes: existing `KmlBuilder.ChunkDays(List<Day>, int)`, `KmlBuilder.BuildKmlFile(List<Day>)`, `GeminiExtractionService(HttpClient http, string apiKey, string? promptOverride = null)`, `GeocodingService(HttpClient http, string apiKey)`.
- Produces:
  - `public sealed record KmlFile(string FileName, string Content)` (namespace `GmapPlanner.Core.Services`)
  - `public static List<KmlFile> KmlBuilder.BuildKmlFiles(List<List<Day>> chunks)`
  - `public static List<string> KmlBuilder.SaveKmlFiles(IEnumerable<KmlFile> files, string outputDir)`
  - `PipelineResult.KmlFiles` (`List<KmlFile>`, default `[]`)
  - `public Task<PipelineResult> PipelineService.GenerateAsync(string filePath, int layersPerFile = AppConfig.MaxLayersPerFile, bool noGeocode = false, ProgressCallback? progress = null, CancellationToken ct = default)` — never touches disk except reading `filePath`; `Files` empty, `OutputDir` `""`.
  - `PipelineService.RunAsync(...)` — unchanged signature and behaviour; now also fills `KmlFiles`.

- [ ] **Step 1: Write the failing KmlBuilder tests**

Append these two tests inside `KmlBuilderTests` in `tests/GmapPlanner.Core.Tests/KmlBuilderTests.cs` (before the final closing `}` of the class):

```csharp
    [Fact]
    public void BuildKmlFiles_NamesByDayRangeAndHoldsXmlInMemory()
    {
        var days = Enumerable.Range(1, 12)
            .Select(n => new Day
            {
                DayNumber = n,
                Locations = [new Location { Name = $"Place {n}", Lat = 35.0, Lng = 139.0 }],
            })
            .ToList();

        var files = KmlBuilder.BuildKmlFiles(KmlBuilder.ChunkDays(days, layersPerFile: 10));

        Assert.Equal(new[] { "1-10.kml", "11-12.kml" }, files.Select(f => f.FileName));
        Assert.StartsWith("<?xml", files[0].Content);
        Assert.Contains("utf-8", files[0].Content.Split('\n')[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Place 10", files[0].Content);
        Assert.DoesNotContain("Place 11", files[0].Content);
        Assert.Contains("Place 12", files[1].Content);
    }

    [Fact]
    public void WriteKmlFiles_WritesExactlyTheInMemoryContent()
    {
        var days = new List<Day>
        {
            new() { DayNumber = 1, Locations = [new Location { Name = "שוק נישיקי, Kyoto", Lat = 35.005, Lng = 135.765 }] },
        };
        var chunks = KmlBuilder.ChunkDays(days, layersPerFile: 10);
        var outputDir = Path.Combine(Path.GetTempPath(), "gmap-planner-tests-" + Guid.NewGuid());

        try
        {
            var expected = Assert.Single(KmlBuilder.BuildKmlFiles(chunks));
            var path = Assert.Single(KmlBuilder.WriteKmlFiles(chunks, outputDir));

            Assert.Equal(expected.FileName, Path.GetFileName(path));
            Assert.Equal(new System.Text.UTF8Encoding(false).GetBytes(expected.Content), File.ReadAllBytes(path));
        }
        finally
        {
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test tests/GmapPlanner.Core.Tests --filter "FullyQualifiedName~KmlBuilderTests"`
Expected: build FAILS with `error CS0117: 'KmlBuilder' does not contain a definition for 'BuildKmlFiles'`.

- [ ] **Step 3: Implement in-memory KML in `KmlBuilder`**

In `src/GmapPlanner.Core/Services/KmlBuilder.cs`, replace the whole existing `WriteKmlFiles` method (lines 100–129, from `/// <summary>Writes each chunk` through its closing `}`) with:

```csharp
    /// <summary>
    /// Serializes each chunk to an in-memory KML file named `{first}.kml` or `{first}-{last}.kml`.
    /// UTF-8 without BOM, two-space indent — byte-identical to what <see cref="SaveKmlFiles"/> writes.
    /// </summary>
    public static List<KmlFile> BuildKmlFiles(List<List<Day>> chunks)
    {
        var files = new List<KmlFile>();
        foreach (var chunk in chunks)
        {
            var serializer = new Serializer();
            serializer.Serialize(BuildKmlFile(chunk));

            var first = chunk[0].DayNumber;
            var last = chunk[^1].DayNumber;
            var filename = first == last ? $"{first}.kml" : $"{first}-{last}.kml";

            using var stream = new MemoryStream();
            using (var writer = XmlWriter.Create(stream, new XmlWriterSettings
            {
                Indent = true,
                IndentChars = "  ",
                Encoding = new UTF8Encoding(false),
            }))
            {
                XDocument.Parse(serializer.Xml).Save(writer);
            }
            files.Add(new KmlFile(filename, Encoding.UTF8.GetString(stream.ToArray())));
        }
        return files;
    }

    /// <summary>Writes in-memory KML files under outputDir; returns the written paths.</summary>
    public static List<string> SaveKmlFiles(IEnumerable<KmlFile> files, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var paths = new List<string>();
        foreach (var file in files)
        {
            var path = Path.Combine(outputDir, file.FileName);
            File.WriteAllText(path, file.Content, new UTF8Encoding(false));
            paths.Add(path);
        }
        return paths;
    }

    /// <summary>Writes each chunk to `{first}.kml` or `{first}-{last}.kml` under outputDir.</summary>
    public static List<string> WriteKmlFiles(List<List<Day>> chunks, string outputDir) =>
        SaveKmlFiles(BuildKmlFiles(chunks), outputDir);
```

Then add the record at the very end of the file, after the closing `}` of `KmlBuilder`:

```csharp

/// <summary>A generated KML file held in memory (desktop saves it; the browser offers it as a download).</summary>
public sealed record KmlFile(string FileName, string Content);
```

- [ ] **Step 4: Run the KmlBuilder tests to verify they pass**

Run: `dotnet test tests/GmapPlanner.Core.Tests --filter "FullyQualifiedName~KmlBuilderTests"`
Expected: PASS, including the pre-existing `WriteKmlFiles_WritesOneFilePerChunkWithPlacemarksAndDayColors`.

- [ ] **Step 5: Write the failing PipelineService tests**

Create `tests/GmapPlanner.Core.Tests/PipelineServiceTests.cs`:

```csharp
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using GmapPlanner.Core.Services;
using GmapPlanner.Core.Services.Gemini;
using Xunit;

namespace GmapPlanner.Core.Tests;

/// <summary>
/// GenerateAsync is the browser path: extract -> geocode -> KML, all in memory. RunAsync is
/// the desktop path layered on top. Geocoding runs with an empty key, which skips the Places
/// API entirely, so the only HTTP call is the faked Gemini one.
/// </summary>
public class PipelineServiceTests
{
    private const string TripJson = """
        {"trip_name": "Tokyo/Kyoto Trip", "days": [
          {"day": 1, "date": "01/06", "locations": [{"name": "Nishiki Market, Kyoto", "lat": 35.005, "lng": 135.765, "notes": ""}]},
          {"day": 2, "date": "02/06", "locations": [{"name": "Senso-ji, Tokyo", "lat": 35.714, "lng": 139.796, "notes": ""}]}
        ]}
        """;

    [Fact]
    public async Task GenerateAsync_ReturnsKmlInMemoryWithoutWritingFiles()
    {
        var result = await Pipeline().GenerateAsync(await TempTxt(), layersPerFile: 10);

        Assert.Equal("Tokyo/Kyoto Trip", result.TripName);
        Assert.Equal(2, result.Days);
        Assert.Equal(2, result.Locations);
        Assert.Equal(2, result.Fallback); // no Places key -> Gemini's coords kept
        var kml = Assert.Single(result.KmlFiles);
        Assert.Equal("1-2.kml", kml.FileName);
        Assert.Contains("Senso-ji, Tokyo", kml.Content);
        Assert.Empty(result.Files);
        Assert.Equal("", result.OutputDir);
    }

    [Fact]
    public async Task RunAsync_SavesTheGeneratedKmlUnderTheTripFolder()
    {
        var outputDir = Path.Combine(Path.GetTempPath(), "gmap-planner-tests-" + Guid.NewGuid());
        try
        {
            var result = await Pipeline().RunAsync(await TempTxt(), outputDir, layersPerFile: 1);

            Assert.Equal(Path.Combine(outputDir, "TokyoKyoto Trip"), result.OutputDir);
            Assert.Equal(new[] { "1.kml", "2.kml" }, result.KmlFiles.Select(f => f.FileName));
            Assert.Equal(result.KmlFiles.Select(f => Path.Combine(result.OutputDir, f.FileName)), result.Files);
            Assert.All(result.Files, p => Assert.True(File.Exists(p)));
        }
        finally
        {
            if (Directory.Exists(outputDir)) Directory.Delete(outputDir, recursive: true);
        }
    }

    private static PipelineService Pipeline()
    {
        var http = new HttpClient(new OneResponseHandler(Envelope(TripJson)));
        return new PipelineService(new GeminiExtractionService(http, apiKey: "fake-key"), new GeocodingService(http, apiKey: ""));
    }

    private static async Task<string> TempTxt()
    {
        var path = Path.Combine(Path.GetTempPath(), $"gmap-planner-test-{Guid.NewGuid()}.txt");
        await File.WriteAllTextAsync(path, "Day 1: Kyoto. Day 2: Tokyo.");
        return path;
    }

    private static string Envelope(string text) => new JsonObject
    {
        ["candidates"] = new JsonArray(new JsonObject
        {
            ["content"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) },
            ["finishReason"] = "STOP",
        }),
    }.ToJsonString();

    private sealed class OneResponseHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }
}
```

- [ ] **Step 6: Run the tests to verify they fail**

Run: `dotnet test tests/GmapPlanner.Core.Tests --filter "FullyQualifiedName~PipelineServiceTests"`
Expected: build FAILS with `error CS1061: 'PipelineService' does not contain a definition for 'GenerateAsync'` (and `'PipelineResult' does not contain a definition for 'KmlFiles'`).

- [ ] **Step 7: Implement `GenerateAsync`**

Replace the entire contents of `src/GmapPlanner.Core/Services/PipelineService.cs` with:

```csharp
using GmapPlanner.Core.Errors;
using GmapPlanner.Core.Services.Gemini;

namespace GmapPlanner.Core.Services;

public record PipelineResult
{
    public required string TripName { get; init; }
    public int Days { get; init; }
    public int Locations { get; init; }
    public int Corrected { get; init; }
    public int Fallback { get; init; }
    public string? GeocodeWarning { get; init; }
    public List<KmlFile> KmlFiles { get; init; } = [];
    public List<string> Files { get; init; } = [];
    public string OutputDir { get; init; } = "";
}

public delegate void ProgressCallback(string step, double fraction);

/// <summary>Ports gmap_planner/service.py's run_pipeline: extract -> geocode -> build KML.</summary>
public class PipelineService(GeminiExtractionService gemini, GeocodingService geocoding)
{
    /// <summary>
    /// Extract -> geocode -> KML, held in memory (the browser host offers the files as
    /// downloads). Nothing is written to disk; Files and OutputDir stay empty.
    /// </summary>
    public async Task<PipelineResult> GenerateAsync(
        string filePath,
        int layersPerFile = AppConfig.MaxLayersPerFile,
        bool noGeocode = false,
        ProgressCallback? progress = null,
        CancellationToken ct = default)
    {
        progress?.Invoke("Extracting locations with Gemini", 0.35);
        var trip = await gemini.ExtractItineraryAsync(filePath, ct);
        if (trip.Days.Count == 0)
            throw new PipelineException("No days found in the extracted itinerary.");

        var corrected = 0;
        var fallback = 0;
        string? geocodeWarning = null;
        if (!noGeocode)
        {
            progress?.Invoke("Snapping place names to exact coordinates", 0.6);
            (corrected, fallback, geocodeWarning) = await geocoding.GeocodeItineraryAsync(trip, ct);
        }

        progress?.Invoke("Building KML files", 0.85);
        var kmlFiles = KmlBuilder.BuildKmlFiles(KmlBuilder.ChunkDays(trip.Days, layersPerFile));

        return new PipelineResult
        {
            TripName = trip.TripName,
            Days = trip.Days.Count,
            Locations = trip.Days.Sum(d => d.Locations.Count),
            Corrected = corrected,
            Fallback = fallback,
            GeocodeWarning = geocodeWarning,
            KmlFiles = kmlFiles,
        };
    }

    /// <summary>Desktop path: <see cref="GenerateAsync"/>, then write the KML files under outputDir/{trip}.</summary>
    public async Task<PipelineResult> RunAsync(
        string filePath,
        string outputDir,
        int layersPerFile = AppConfig.MaxLayersPerFile,
        bool noGeocode = false,
        ProgressCallback? progress = null,
        CancellationToken ct = default)
    {
        var result = await GenerateAsync(filePath, layersPerFile, noGeocode, progress, ct);

        var tripDir = Path.Combine(outputDir, KmlBuilder.SanitizeFolderName(result.TripName));
        var files = KmlBuilder.SaveKmlFiles(result.KmlFiles, tripDir);

        progress?.Invoke("Done", 1.0);
        return result with { Files = files, OutputDir = tripDir };
    }
}
```

- [ ] **Step 8: Run the full test suite**

Run: `dotnet test tests/GmapPlanner.Core.Tests`
Expected: PASS — all previously passing tests (88) plus the 4 new ones = 92 passed, 0 failed.

- [ ] **Step 9: Build the whole solution**

Close any running `GmapPlanner.App`, then run: `dotnet build`
Expected: `Build succeeded.` with `0 Error(s)` (the desktop app still calls `RunAsync`, whose signature is unchanged).

- [ ] **Step 10: Commit**

```bash
git add src/GmapPlanner.Core/Services/KmlBuilder.cs src/GmapPlanner.Core/Services/PipelineService.cs tests/GmapPlanner.Core.Tests/KmlBuilderTests.cs tests/GmapPlanner.Core.Tests/PipelineServiceTests.cs
git commit -m "Core: generate KML in memory (KmlFile, GenerateAsync); RunAsync saves on top

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 2: Split publishing dependencies into `GmapPlanner.Core.Publish`

**Files:**
- Create: `src/GmapPlanner.Core.Publish/GmapPlanner.Core.Publish.csproj`
- Create: `src/GmapPlanner.Core.Publish/Json/PublishJsonContext.cs`
- Create: `tests/GmapPlanner.Core.Tests/CoreDependencyTests.cs`
- Move: `src/GmapPlanner.Core/Services/Publish/{DriveShareService,MyMapsImport,MyMapsSelectors,MyMapsSession,PublishService}.cs` → `src/GmapPlanner.Core.Publish/Services/Publish/`
- Move: `src/GmapPlanner.Core/Services/UsageService.cs`, `src/GmapPlanner.Core/Services/SheetsAnalyticsService.cs` → `src/GmapPlanner.Core.Publish/Services/`
- Modify: `src/GmapPlanner.Core/GmapPlanner.Core.csproj`
- Modify: `src/GmapPlanner.Core/Json/GmapPlannerJsonContext.cs:22`
- Modify: `src/GmapPlanner.Core.Publish/Services/UsageService.cs:91-92` (after the move)
- Modify: `src/GmapPlanner.App/GmapPlanner.App.csproj:69-71`
- Modify: `tests/GmapPlanner.Core.Tests/GmapPlanner.Core.Tests.csproj:25-27`
- Modify: `GmapPlanner.sln`, `CLAUDE.md`

**Interfaces:**
- Consumes: Task 1's `KmlBuilder` (used only as a type anchor for the `Core` assembly in the guard test).
- Produces: assembly `GmapPlanner.Core.Publish` containing, with **unchanged namespaces and public APIs**: `MyMapsSession`, `MyMapsSelectors`, `MyMapsImport` (internal), `PublishService`, `DriveShareService` (namespace `GmapPlanner.Core.Services.Publish`); `UsageService`, `UsageGauge`, `SheetsAnalyticsService`, `AnalyticsRow` (namespace `GmapPlanner.Core.Services`). `GmapPlanner.Core` references neither `Microsoft.Playwright` nor any `Google.Apis*` assembly. Everything else stays in `Core` (`AppConfig`, `AppDataPaths`, `UsageRing`, `AppSettingsService`, `SetupBundleService`, `UpdateService`, Gemini, Places, KML, Pipeline).

- [ ] **Step 1: Write the failing dependency guard test**

Create `tests/GmapPlanner.Core.Tests/CoreDependencyTests.cs`:

```csharp
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
```

- [ ] **Step 2: Run the guard test to verify it fails**

Run: `dotnet test tests/GmapPlanner.Core.Tests --filter "FullyQualifiedName~CoreDependencyTests"`
Expected: FAIL — `Assert.DoesNotContain() Failure` (Core currently references `Microsoft.Playwright` and `Google.Apis.*`).

- [ ] **Step 3: Move the files with git**

```bash
mkdir -p src/GmapPlanner.Core.Publish/Services/Publish src/GmapPlanner.Core.Publish/Json
git mv src/GmapPlanner.Core/Services/Publish/DriveShareService.cs src/GmapPlanner.Core.Publish/Services/Publish/DriveShareService.cs
git mv src/GmapPlanner.Core/Services/Publish/MyMapsImport.cs src/GmapPlanner.Core.Publish/Services/Publish/MyMapsImport.cs
git mv src/GmapPlanner.Core/Services/Publish/MyMapsSelectors.cs src/GmapPlanner.Core.Publish/Services/Publish/MyMapsSelectors.cs
git mv src/GmapPlanner.Core/Services/Publish/MyMapsSession.cs src/GmapPlanner.Core.Publish/Services/Publish/MyMapsSession.cs
git mv src/GmapPlanner.Core/Services/Publish/PublishService.cs src/GmapPlanner.Core.Publish/Services/Publish/PublishService.cs
git mv src/GmapPlanner.Core/Services/UsageService.cs src/GmapPlanner.Core.Publish/Services/UsageService.cs
git mv src/GmapPlanner.Core/Services/SheetsAnalyticsService.cs src/GmapPlanner.Core.Publish/Services/SheetsAnalyticsService.cs
```

Expected: `git status` shows 7 `renamed:` entries. (If the working tree has uncommitted edits in `UsageService.cs`/`SheetsAnalyticsService.cs`, `git mv` carries them along — do not discard them.)

- [ ] **Step 4: Create the `Core.Publish` project file**

Create `src/GmapPlanner.Core.Publish/GmapPlanner.Core.Publish.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <!-- This library builds RID-agnostic, so Microsoft.Playwright.targets would resolve the
         node driver off the BUILD HOST (win32_x64 on Windows) and leak it into the app's
         osx-arm64 publish. A library must bundle no driver at all — the published app
         (which knows its target RID) copies the right one. 'none' skips the copy; the
         managed Microsoft.Playwright.dll still flows through the project reference. -->
    <PlaywrightPlatform>none</PlaywrightPlatform>
  </PropertyGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="GmapPlanner.Core.Tests" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Google.Apis.Drive.v3" Version="1.75.0.4218" />
    <PackageReference Include="Microsoft.Playwright" Version="1.61.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\GmapPlanner.Core\GmapPlanner.Core.csproj" />
  </ItemGroup>

</Project>
```

- [ ] **Step 5: Strip the publish dependencies from `Core`**

Replace the entire contents of `src/GmapPlanner.Core/GmapPlanner.Core.csproj` with:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <!-- Browser-safe: no Playwright, no Google.Apis (those live in GmapPlanner.Core.Publish).
       CoreDependencyTests fails the build's tests if either creeps back in. -->

  <ItemGroup>
    <InternalsVisibleTo Include="GmapPlanner.Core.Tests" />
    <InternalsVisibleTo Include="GmapPlanner.Core.Publish" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="SharpKml.Core" Version="6.1.0" />
  </ItemGroup>

</Project>
```

- [ ] **Step 6: Give `UsageService` its own source-generated JSON context**

In `src/GmapPlanner.Core/Json/GmapPlannerJsonContext.cs`, delete this line:

```csharp
[JsonSerializable(typeof(TimeSeriesResponse))]
```

Create `src/GmapPlanner.Core.Publish/Json/PublishJsonContext.cs`:

```csharp
using System.Text.Json.Serialization;
using GmapPlanner.Core.Services;

namespace GmapPlanner.Core.Json;

/// <summary>
/// Source-generated JSON type info for GmapPlanner.Core.Publish's own DTOs. Same trimming
/// rule as GmapPlannerJsonContext: reflection-based System.Text.Json is off in every build.
/// </summary>
[JsonSerializable(typeof(TimeSeriesResponse))]
internal partial class PublishJsonContext : JsonSerializerContext;
```

In `src/GmapPlanner.Core.Publish/Services/UsageService.cs`, change:

```csharp
        var payload = await response.Content.ReadFromJsonAsync(
            GmapPlannerJsonContext.Default.TimeSeriesResponse, cts.Token);
```

to:

```csharp
        var payload = await response.Content.ReadFromJsonAsync(
            PublishJsonContext.Default.TimeSeriesResponse, cts.Token);
```

- [ ] **Step 7: Reference `Core.Publish` from the app and the tests; register it in the solution**

In `src/GmapPlanner.App/GmapPlanner.App.csproj`, replace:

```xml
  <ItemGroup>
    <ProjectReference Include="..\GmapPlanner.Core\GmapPlanner.Core.csproj" />
  </ItemGroup>
```

with:

```xml
  <ItemGroup>
    <ProjectReference Include="..\GmapPlanner.Core\GmapPlanner.Core.csproj" />
    <ProjectReference Include="..\GmapPlanner.Core.Publish\GmapPlanner.Core.Publish.csproj" />
  </ItemGroup>
```

In `tests/GmapPlanner.Core.Tests/GmapPlanner.Core.Tests.csproj`, replace:

```xml
  <ItemGroup>
    <ProjectReference Include="..\..\src\GmapPlanner.Core\GmapPlanner.Core.csproj" />
  </ItemGroup>
```

with:

```xml
  <ItemGroup>
    <ProjectReference Include="..\..\src\GmapPlanner.Core\GmapPlanner.Core.csproj" />
    <ProjectReference Include="..\..\src\GmapPlanner.Core.Publish\GmapPlanner.Core.Publish.csproj" />
  </ItemGroup>
```

Run: `dotnet sln GmapPlanner.sln add src/GmapPlanner.Core.Publish/GmapPlanner.Core.Publish.csproj --solution-folder src`
Expected: `Project ... added to the solution.`

- [ ] **Step 8: Build and run all tests**

Close any running `GmapPlanner.App`, then run: `dotnet build`
Expected: `Build succeeded.` `0 Error(s)`.

Run: `dotnet test tests/GmapPlanner.Core.Tests`
Expected: 93 passed, 0 failed (92 from Task 1 + `Core_DoesNotReferencePlaywrightOrGoogleApis`).

- [ ] **Step 9: Verify the Playwright driver still lands correctly in a desktop publish**

Run: `dotnet publish src/GmapPlanner.App -c Release -r win-x64 -o publish/phase1-win-x64`
Then run: `ls publish/phase1-win-x64/.playwright/node/`
Expected: exactly `LICENSE` and `win32_x64` (no `darwin-*`, no `linux-*`). Delete the folder afterwards: `rm -rf publish/phase1-win-x64`.

- [ ] **Step 10: Update CLAUDE.md for the new project**

In `CLAUDE.md`, replace the `## Solution structure` code block's `src/` part — from the line `  GmapPlanner.Core/          # no UI references — models, services, prompt` through the line `    AppDataPaths.cs           # per-OS app data dir (ports paths.py)` — with:

```
  GmapPlanner.Core/          # browser-safe: no UI, no Playwright, no Google.Apis
    Models/                  # Trip, Day, Location
    Errors/                  # PipelineException, BadResponseException
    Prompt/                  # ExtractionPrompt (ported verbatim from prompt.py)
    Services/
      Gemini/                # GeminiExtractionService (HttpClient, no SDK)
      GeocodingService.cs    # Places API (New) Text Search
      KmlBuilder.cs          # SharpKml.Core; KmlFile = in-memory KML
      PipelineService.cs     # GenerateAsync (in memory) / RunAsync (+ write KML)
    AppConfig.cs              # GEMINI_MODEL, MAX_LAYERS_PER_FILE, DAY_COLORS, ...
    AppDataPaths.cs           # per-OS app data dir (ports paths.py)
  GmapPlanner.Core.Publish/  # everything needing Playwright or Google.Apis
    Services/
      Publish/               # phase 2
        MyMapsSession.cs     # Playwright automation of the My Maps editor
        MyMapsSelectors.cs   # the selectors Google keeps breaking
        MyMapsImport.cs      # the import retry, behind IImportSurface so it's testable
        DriveShareService.cs # OAuth, permissions.create, copyRequiresWriterPermission
        PublishService.cs    # one map per KML file, then share
      UsageService.cs        # Cloud Monitoring usage ring
      SheetsAnalyticsService.cs
    Json/PublishJsonContext.cs
```

In the same file, under `### Playwright's node driver is per-platform`, replace:

```
1. **Library leaks the host driver.** `GmapPlanner.Core` holds the `Microsoft.Playwright`
   PackageReference but builds RID-agnostic, so `Microsoft.Playwright.targets` resolved
   the driver off the *build host* (win32_x64) and it rode into the mac publish. Core sets
   `<PlaywrightPlatform>none</PlaywrightPlatform>` — a library bundles no driver; the app does.
```

with:

```
1. **Library leaks the host driver.** `GmapPlanner.Core.Publish` holds the `Microsoft.Playwright`
   PackageReference but builds RID-agnostic, so `Microsoft.Playwright.targets` resolved
   the driver off the *build host* (win32_x64) and it rode into the mac publish. Core.Publish sets
   `<PlaywrightPlatform>none</PlaywrightPlatform>` — a library bundles no driver; the app does.
```

and replace `the fixes in \`GmapPlanner.Core.csproj\` / \`GmapPlanner.App.csproj\`` with `the fixes in \`GmapPlanner.Core.Publish.csproj\` / \`GmapPlanner.App.csproj\``.

- [ ] **Step 11: Commit**

```bash
git add -A src/GmapPlanner.Core src/GmapPlanner.Core.Publish src/GmapPlanner.App/GmapPlanner.App.csproj tests/GmapPlanner.Core.Tests GmapPlanner.sln CLAUDE.md
git status --short
```

Expected in `git status`: the 7 renames, the new csproj/context/test, modified Core csproj, JSON context, App csproj, tests csproj, sln, CLAUDE.md. Nothing unrelated staged (`.gitignore`, `.claude/`, `tests/*.kml` must stay unstaged). If an unrelated file is staged, `git restore --staged <file>`.

```bash
git commit -m "Split Playwright/Google.Apis code into GmapPlanner.Core.Publish

Core is now browser-safe for the Avalonia WASM host; CoreDependencyTests guards it.
Namespaces unchanged.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 3: Shared `GmapPlanner.UI` library with `MainView`

**Files:**
- Create: `src/GmapPlanner.UI/GmapPlanner.UI.csproj`
- Create: `src/GmapPlanner.UI/Theme.axaml` (generated from `src/GmapPlanner.App/App.axaml`)
- Create: `src/GmapPlanner.UI/Views/MainView.axaml` (generated from `src/GmapPlanner.App/Views/MainWindow.axaml`)
- Move + rewrite: `src/GmapPlanner.App/Views/MainWindow.axaml.cs` → `src/GmapPlanner.UI/Views/MainView.axaml.cs`
- Move: `src/GmapPlanner.App/ViewModels/*.cs` → `src/GmapPlanner.UI/ViewModels/`
- Rewrite: `src/GmapPlanner.App/App.axaml`, `src/GmapPlanner.App/Views/MainWindow.axaml`, `src/GmapPlanner.App/Views/MainWindow.axaml.cs`
- Modify: `src/GmapPlanner.App/GmapPlanner.App.csproj`
- Modify: `GmapPlanner.sln`, `CLAUDE.md`, `docs/superpowers/specs/2026-09-15-cloud-hosting-design.md`

**Interfaces:**
- Consumes: Task 2's `GmapPlanner.Core.Publish` project (the view model still calls `PublishService`, `MyMapsSession`, `UsageService`, `SheetsAnalyticsService`, `UpdateService` directly — phase 2 introduces the local/cloud publish seam).
- Produces:
  - assembly `GmapPlanner.UI` with `GmapPlanner.App.Views.MainView : UserControl` (whole app UI; expects `DataContext` of type `GmapPlanner.App.ViewModels.MainViewModel`) and all view models/converters (`MainViewModel`, `KmlFileItem`, `AnalyticsBar`, `BoolTick`, `ViewModelBase`).
  - `avares://GmapPlanner.UI/Theme.axaml` — a `Styles` file holding every design token (as `Styles.Resources`) and component style. A host includes it **after** `<FluentTheme />`.
  - desktop `GmapPlanner.App.Views.MainWindow : Window` that only hosts `<views:MainView />`.

- [ ] **Step 1: Create the UI project and register it**

Create `src/GmapPlanner.UI/GmapPlanner.UI.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <!-- Shared Avalonia UI: MainView, view models, and the design system (Theme.axaml).
       Hosted by the desktop exe (GmapPlanner.App) today and by the browser host in phase 2.
       Namespaces stay GmapPlanner.App.* so XAML xmlns and usings didn't have to move. -->
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Avalonia" Version="11.2.5" />
    <PackageReference Include="Avalonia.Themes.Fluent" Version="11.2.5" />
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.2" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\GmapPlanner.Core\GmapPlanner.Core.csproj" />
    <ProjectReference Include="..\GmapPlanner.Core.Publish\GmapPlanner.Core.Publish.csproj" />
  </ItemGroup>

</Project>
```

Run: `dotnet sln GmapPlanner.sln add src/GmapPlanner.UI/GmapPlanner.UI.csproj --solution-folder src`
Expected: `Project ... added to the solution.`

- [ ] **Step 2: Move the view models**

```bash
mkdir -p src/GmapPlanner.UI/ViewModels src/GmapPlanner.UI/Views
git mv src/GmapPlanner.App/ViewModels/AnalyticsBar.cs src/GmapPlanner.UI/ViewModels/AnalyticsBar.cs
git mv src/GmapPlanner.App/ViewModels/BoolTick.cs src/GmapPlanner.UI/ViewModels/BoolTick.cs
git mv src/GmapPlanner.App/ViewModels/KmlFileItem.cs src/GmapPlanner.UI/ViewModels/KmlFileItem.cs
git mv src/GmapPlanner.App/ViewModels/MainViewModel.cs src/GmapPlanner.UI/ViewModels/MainViewModel.cs
git mv src/GmapPlanner.App/ViewModels/ViewModelBase.cs src/GmapPlanner.UI/ViewModels/ViewModelBase.cs
```

Expected: `git status` shows 5 `renamed:` entries.

- [ ] **Step 3: Generate `Theme.axaml` from the current `App.axaml`**

Before touching `App.axaml`, confirm the line anchors: `sed -n '10p;56p;62p;240p;241p' src/GmapPlanner.App/App.axaml`
Expected output, in order: `        <ResourceDictionary>`, `        </ResourceDictionary>`, `        <!-- ============ TYPOGRAPHY + COMPONENT STYLES ============ -->`, `        </Style>`, `    </Application.Styles>`. If any line differs, stop and re-derive the ranges (lines 10–56 = the resource dictionary; 62–240 = every style after `<FluentTheme />`).

Run (Git Bash):

```bash
{
cat <<'EOF'
<Styles xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <!-- Shared design system for every host (desktop window, browser). Tokens live in
         Styles.Resources so {StaticResource} lookups resolve app-wide once a host includes
         this file after <FluentTheme />. Dark-first: hosts set RequestedThemeVariant="Dark". -->
    <Styles.Resources>
EOF
sed -n '10,56p' src/GmapPlanner.App/App.axaml
echo '    </Styles.Resources>'
echo
sed -n '62,240p' src/GmapPlanner.App/App.axaml
echo '</Styles>'
} > src/GmapPlanner.UI/Theme.axaml
```

Run: `grep -c "x:Key=" src/GmapPlanner.UI/Theme.axaml` — Expected: `34` (same as `grep -c "x:Key=" src/GmapPlanner.App/App.axaml`).
Run: `grep -c "<Style Selector" src/GmapPlanner.UI/Theme.axaml` — Expected: equal to `grep -c "<Style Selector" src/GmapPlanner.App/App.axaml`.

- [ ] **Step 4: Rewrite the desktop `App.axaml` as a thin host**

Replace the entire contents of `src/GmapPlanner.App/App.axaml` with:

```xml
<Application xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="GmapPlanner.App.App"
             RequestedThemeVariant="Dark">
    <!-- Dark-first: Theme.axaml's palette is tuned for a single dark surface. The whole
         design system (tokens + component styles) lives in GmapPlanner.UI so the browser
         host shares it. -->
    <Application.Styles>
        <FluentTheme />
        <StyleInclude Source="avares://GmapPlanner.UI/Theme.axaml" />
    </Application.Styles>
</Application>
```

(`src/GmapPlanner.App/App.axaml.cs` is unchanged: it still creates `MainWindow` with a `MainViewModel` DataContext.)

- [ ] **Step 5: Generate `MainView.axaml` from the current `MainWindow.axaml`**

Confirm anchors: `sed -n '19p;25p;546p;547p' src/GmapPlanner.App/Views/MainWindow.axaml`
Expected output, in order: `    <Design.DataContext>`, `    <Grid Name="RootGrid">`, `    </Grid>`, `</Window>`. If any differs, stop and re-derive (19 = start of `Design.DataContext`; 546 = the root Grid's closing tag).

Run (Git Bash):

```bash
{
cat <<'EOF'
<UserControl xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:vm="using:GmapPlanner.App.ViewModels"
             xmlns:sys="using:System"
             xmlns:d="http://schemas.microsoft.com/expression/blend/2008"
             xmlns:mc="http://schemas.openxmlformats.org/markup-compatibility/2006"
             mc:Ignorable="d" d:DesignWidth="1020" d:DesignHeight="720"
             x:Class="GmapPlanner.App.Views.MainView"
             x:DataType="vm:MainViewModel"
             x:CompileBindings="True"
             Background="{StaticResource BgBrush}"
             FontFamily="Inter, Segoe UI Variable Text, Segoe UI"
             FontSize="14">
    <!-- The whole app UI, host-agnostic: the desktop MainWindow wraps it today, the
         browser host (phase 2) uses it as its single view. -->

EOF
sed -n '19,546p' src/GmapPlanner.App/Views/MainWindow.axaml
echo '</UserControl>'
} > src/GmapPlanner.UI/Views/MainView.axaml
```

Run: `grep -o ' Name="[^"]*"' src/GmapPlanner.UI/Views/MainView.axaml | wc -l` — Expected: `11` (every named control carried over; the window's unused `x:Name="Root"` is intentionally dropped).

- [ ] **Step 6: Move and rewrite the code-behind as `MainView`**

```bash
git mv src/GmapPlanner.App/Views/MainWindow.axaml.cs src/GmapPlanner.UI/Views/MainView.axaml.cs
```

Replace the entire contents of `src/GmapPlanner.UI/Views/MainView.axaml.cs` with (same handlers as before; `Window.StorageProvider` becomes the hosting `TopLevel`'s, which works in both a window and the browser):

```csharp
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using GmapPlanner.App.ViewModels;

namespace GmapPlanner.App.Views;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();

        BrowseInputButton.Click += async (_, _) => await SafeAsync(BrowseInputFileAsync);
        DownloadButton.Click += async (_, _) => await SafeAsync(DownloadKmlFilesAsync);
        SetupBundleButton.Click += async (_, _) => await SafeAsync(BrowseSetupBundleAsync);
        CredentialsButton.Click += async (_, _) => await SafeAsync(BrowseCredentialsAsync);

        // Email token input: Enter/Tab/separators commit a chip; Backspace on empty pops one;
        // losing focus commits whatever's half-typed so it isn't silently lost.
        EmailEntry.KeyDown += OnEmailEntryKeyDown;
        EmailEntry.LostFocus += (_, _) => CommitEmailEntry();

        DragDrop.SetAllowDrop(DropZone, true);
        DropZone.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        DropZone.AddHandler(DragDrop.DropEvent, OnDrop);

        // Drop a file straight onto the Settings pickers instead of browsing for it.
        EnableFileDrop(SetupBundleButton, (vm, p) => vm.ApplySetupBundleFile(p));
        EnableFileDrop(CredentialsButton, (vm, p) => vm.SetDriveCredentialsFile(p));

        // Hold the eye to reveal a masked API key; release (or leave) re-masks it.
        WireHoldReveal(GeminiKeyEye, GeminiKeyBox);
        WireHoldReveal(GeoKeyEye, GeoKeyBox);

        // Load the usage gauge once the view is up, on the UI thread so binding is safe.
        Loaded += async (_, _) =>
        {
            if (DataContext is MainViewModel vm) await vm.RefreshUsageAsync();
        };
    }

    /// <summary>The hosting window's (or browser's) storage provider, for the file/folder pickers.</summary>
    private IStorageProvider Storage => TopLevel.GetTopLevel(this)!.StorageProvider;

    /// <summary>Cap the resizable sidebar at a third of the view; clamp if the view shrinks.</summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var sidebar = RootGrid.ColumnDefinitions[0];
        var max = Math.Max(sidebar.MinWidth, e.NewSize.Width / 3);
        sidebar.MaxWidth = max;
        if (sidebar.Width.IsAbsolute && sidebar.Width.Value > max)
            sidebar.Width = new Avalonia.Controls.GridLength(max);
    }

    private static FilePickerOpenOptions JsonPicker(string title) => new()
    {
        Title = title,
        AllowMultiple = false,
        FileTypeFilter = [new FilePickerFileType("JSON (*.json)") { Patterns = ["*.json"] }],
    };

    private async Task BrowseSetupBundleAsync()
    {
        if (DataContext is not MainViewModel vm) return;
        var files = await Storage.OpenFilePickerAsync(JsonPicker("Choose a setup file"));
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null) vm.ApplySetupBundleFile(path);
    }

    private async Task BrowseCredentialsAsync()
    {
        if (DataContext is not MainViewModel vm) return;
        var files = await Storage.OpenFilePickerAsync(JsonPicker("Choose the Drive credentials.json"));
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null) vm.SetDriveCredentialsFile(path);
    }

    /// <summary>
    /// Click handlers are async void, so anything thrown inside one takes the whole
    /// process down instead of surfacing. Show it on the page instead.
    /// </summary>
    private async Task SafeAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception e) when (DataContext is MainViewModel vm)
        {
            vm.ErrorText = e.Message;
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None;

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var path = e.Data.GetFiles()?.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null) vm.SetInputFile(path);
    }

    /// <summary>Lets a control accept a dropped file, handing the first local path to the VM.</summary>
    private void EnableFileDrop(Control target, Action<MainViewModel, string> onFile)
    {
        DragDrop.SetAllowDrop(target, true);
        target.AddHandler(DragDrop.DragOverEvent, (_, e) =>
            e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None);
        target.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (DataContext is not MainViewModel vm) return;
            var path = e.Data.GetFiles()?.FirstOrDefault()?.TryGetLocalPath();
            if (path is not null) onFile(vm, path);
        });
    }

    /// <summary>Reveals a password TextBox while the eye is held (tunnel, so the Button can't swallow it).</summary>
    private static void WireHoldReveal(Control eye, TextBox box)
    {
        eye.AddHandler(InputElement.PointerPressedEvent, (_, _) => box.RevealPassword = true, RoutingStrategies.Tunnel);
        eye.AddHandler(InputElement.PointerReleasedEvent, (_, _) => box.RevealPassword = false, RoutingStrategies.Tunnel);
        // Releasing off the button (drag away) still fires PointerExited — re-mask there too.
        eye.PointerExited += (_, _) => box.RevealPassword = false;
    }

    private async Task BrowseInputFileAsync()
    {
        if (DataContext is not MainViewModel vm) return;

        var files = await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose an itinerary",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Itinerary (*.pdf, *.txt)") { Patterns = ["*.pdf", "*.txt"] },
            ],
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null) vm.SetInputFile(path);
    }

    private async Task DownloadKmlFilesAsync()
    {
        if (DataContext is not MainViewModel vm || vm.ResultFiles.Count == 0) return;

        var folders = await Storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Save KML files to…",
            AllowMultiple = false,
        });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (path is not null) vm.SaveKmlFilesTo(path);
    }

    private void OnEmailEntryKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;

        // Backspace with nothing typed removes the last chip.
        if (e.Key == Key.Back && string.IsNullOrEmpty(tb.Text))
        {
            (DataContext as MainViewModel)?.RemoveLastEmail();
            return;
        }

        // Enter / Tab / , ; commit the typed text as chip(s).
        if (e.Key is Key.Enter or Key.Tab or Key.OemComma or Key.OemSemicolon)
        {
            if (CommitEmailEntry()) e.Handled = true;
        }
    }

    /// <summary>Turns whatever is in the entry box into chips and clears it. True if it had text.</summary>
    private bool CommitEmailEntry()
    {
        if (DataContext is not MainViewModel vm || string.IsNullOrWhiteSpace(EmailEntry.Text)) return false;
        vm.AddEmails(EmailEntry.Text);
        EmailEntry.Text = "";
        return true;
    }
}
```

- [ ] **Step 7: Rewrite the desktop `MainWindow` as a thin shell**

Replace the entire contents of `src/GmapPlanner.App/Views/MainWindow.axaml` with:

```xml
<Window xmlns="https://github.com/avaloniaui"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:views="using:GmapPlanner.App.Views"
        x:Class="GmapPlanner.App.Views.MainWindow"
        Icon="/Assets/icon.ico"
        Width="1020" Height="720" MinWidth="900" MinHeight="620"
        Background="{StaticResource BgBrush}"
        Title="My Maps Generator">
    <!-- Desktop shell only: the UI itself is GmapPlanner.UI's MainView, which inherits
         this window's DataContext (a MainViewModel, set in App.axaml.cs). -->
    <views:MainView />
</Window>
```

Create `src/GmapPlanner.App/Views/MainWindow.axaml.cs`:

```csharp
using Avalonia.Controls;

namespace GmapPlanner.App.Views;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();
}
```

- [ ] **Step 8: Point the desktop project at the UI library**

In `src/GmapPlanner.App/GmapPlanner.App.csproj`:

Delete this line (the view models moved to `GmapPlanner.UI`, which references it):

```xml
    <PackageReference Include="CommunityToolkit.Mvvm" Version="8.4.2" />
```

Replace:

```xml
  <ItemGroup>
    <ProjectReference Include="..\GmapPlanner.Core\GmapPlanner.Core.csproj" />
    <ProjectReference Include="..\GmapPlanner.Core.Publish\GmapPlanner.Core.Publish.csproj" />
  </ItemGroup>
```

with:

```xml
  <ItemGroup>
    <ProjectReference Include="..\GmapPlanner.UI\GmapPlanner.UI.csproj" />
  </ItemGroup>
```

(`Program.cs` still uses `GmapPlanner.Core.AppDataPaths`; Core flows transitively through the UI reference.)

- [ ] **Step 9: Build and test**

Close any running `GmapPlanner.App`, then run: `dotnet build`
Expected: `Build succeeded.` `0 Error(s)`. A failure like `AVLN2000 Unable to resolve type` or `Unable to find resource 'BgBrush'` means Step 3/5 generation went wrong — diff the generated file against the anchors.

Run: `dotnet test tests/GmapPlanner.Core.Tests`
Expected: 93 passed, 0 failed.

- [ ] **Step 10: Smoke-test the desktop app by hand**

Run: `dotnet run --project src/GmapPlanner.App`

Check each, and fix before continuing if any fails:
1. Window opens at 1020×720 with the dark slate palette, sidebar, "API USAGE"/"OPTIONS" headers, blue accent buttons (theme include works).
2. Dragging the sidebar splitter past a third of the window width stops at a third (`OnSizeChanged` moved correctly).
3. Settings page: the eye buttons reveal the Gemini/Places keys while held; "Choose setup file…" opens a JSON picker.
4. Make-map page: "Browse" opens the itinerary picker; dropping a `.txt` onto the drop zone selects it.
5. Generate a map from a small `.txt` itinerary: progress runs, results show Days/Locations/Exact coords cards and per-file cards; "Download" copies the KML files into a chosen folder.
6. Email chips: typing an address + Enter adds a chip; Backspace on empty removes it.

Close the app.

- [ ] **Step 11: Verify the trimmed publish**

Run: `dotnet publish src/GmapPlanner.App -c Release -r win-x64 -o publish/phase1-win-x64 2>&1 | tee publish-phase1.log`
Then run: `grep "IL2026" publish-phase1.log | grep -v "Avalonia" ; ls publish/phase1-win-x64/.playwright/node/`
Expected: no IL2026 lines outside Avalonia's own assemblies; the `ls` prints exactly `LICENSE` and `win32_x64`.

Run the published exe: `./publish/phase1-win-x64/GmapPlanner.App.exe` — the window must open styled exactly as in Step 10 item 1 (a trimmed build that loses `Theme.axaml` renders unstyled). Close it, then clean up: `rm -rf publish/phase1-win-x64 publish-phase1.log`.

- [ ] **Step 12: Update CLAUDE.md and the spec**

In `CLAUDE.md`, in the `## Solution structure` code block, replace:

```
  GmapPlanner.App/            # Avalonia MVVM desktop app
    ViewModels/MainViewModel.cs
    Views/MainWindow.axaml(.cs)
```

with:

```
  GmapPlanner.UI/             # shared Avalonia UI (desktop today, browser in phase 2)
    Theme.axaml               # design tokens + component styles; hosts include it after FluentTheme
    ViewModels/MainViewModel.cs
    Views/MainView.axaml(.cs) # the whole app UI as a UserControl
  GmapPlanner.App/            # desktop exe host: Program, App.axaml, thin MainWindow, Assets
```

In the `## UI` section, replace `` `MainWindow.axaml` mirrors the original Streamlit app `` with `` `MainView.axaml` (in `GmapPlanner.UI`, hosted by the desktop `MainWindow`) mirrors the original Streamlit app ``, and replace `` need the `TopLevel`'s `StorageProvider` and so live in `MainWindow.axaml.cs` `` with `` need the `TopLevel`'s `StorageProvider` (via `TopLevel.GetTopLevel(this)`) and so live in `MainView.axaml.cs` ``.

In `## Trimming rules`, rule 2, replace `` `MainWindow.axaml` sets `x:CompileBindings="True"` `` with `` `MainView.axaml` sets `x:CompileBindings="True"` ``.

In `docs/superpowers/specs/2026-09-15-cloud-hosting-design.md`, replace the whole `### \`GmapPlanner.App\` (split)` section heading and its three bullets' project names so they read: `### UI projects`, then `**\`GmapPlanner.UI\`** (shared)` for the first bullet, `**\`GmapPlanner.App\`** (desktop host, path and assembly name unchanged so packaging is untouched)` for the second, and keep `**\`GmapPlanner.App.Browser\`**` for the third. In `## Build order` item 1, replace `split \`App\` → \`App\` + \`App.Desktop\`` with `extract shared UI into \`GmapPlanner.UI\` (desktop host stays \`GmapPlanner.App\`)`.

- [ ] **Step 13: Commit**

```bash
git add -A src/GmapPlanner.UI src/GmapPlanner.App GmapPlanner.sln CLAUDE.md docs/superpowers/specs/2026-09-15-cloud-hosting-design.md
git status --short
```

Expected: 5 view-model renames, `MainWindow.axaml.cs` → `MainView.axaml.cs` rename, new `GmapPlanner.UI.csproj`/`Theme.axaml`/`MainView.axaml`/`MainWindow.axaml.cs`, modified `App.axaml`/`MainWindow.axaml`/App csproj/sln/CLAUDE.md/spec. Nothing unrelated staged.

```bash
git commit -m "Extract shared UI into GmapPlanner.UI (MainView + Theme); desktop MainWindow is a thin host

Prepares the Avalonia browser host. Desktop exe path/assembly unchanged, so release
packaging is untouched.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

## What phase 2 inherits

- `PipelineService.GenerateAsync` still takes a **file path**; the browser has bytes. Phase 2 adds a bytes/stream input to `GeminiExtractionService` and `GenerateAsync` (and the PDF CORS check from the spec).
- `MainViewModel` still calls desktop-only services (`PublishService`, `MyMapsSession`, `UpdateService`, `AppSettingsService`, `Process.Start`, file paths). Phase 2 introduces the publish seam — local (desktop) vs cloud-job (browser), two real implementations — and moves settings persistence behind the host.
- `GmapPlanner.UI` references `GmapPlanner.Core.Publish`; the browser host must not load it, so phase 2 drops that reference once the seam exists.
