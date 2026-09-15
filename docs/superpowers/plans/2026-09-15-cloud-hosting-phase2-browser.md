# Cloud Hosting — Phase 2: Browser Host Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the first live website: the Avalonia app compiled to WebAssembly, hosted as static files on Vercel, generating KML entirely in the browser (Gemini → Places → KML → download). Desktop behaviour unchanged.

**Architecture:** (1) Core accepts itinerary **bytes** (the browser has no file paths) and can send PDFs inline instead of through the Files API upload (which needs a response header browsers can't read cross-origin). (2) Pure helpers move into Core so both hosts share them (settings JSON, setup-bundle merge, analytics sheet ids, usage/analytics records). (3) `MainViewModel` stops calling desktop-only code directly: everything host-specific goes through `IPlatformServices` — two real implementations, `DesktopPlatformServices` (in the desktop exe, wraps Playwright/Drive/Sheets/Monitoring/updater/file system) and `BrowserPlatformServices` (localStorage, JS downloads, features off). `GmapPlanner.UI` drops its `GmapPlanner.Core.Publish` reference. (4) New `GmapPlanner.App.Browser` WASM host. (5) A GitHub Actions workflow builds the WASM bundle and deploys the static output to Vercel.

**Tech Stack:** .NET 8, Avalonia 11.2.5 + Avalonia.Browser 11.2.5 (`net8.0-browser`, `Microsoft.NET.Sdk.WebAssembly`), `System.Runtime.InteropServices.JavaScript` (`[JSImport]`), CommunityToolkit.Mvvm 8.4.2, xunit 2.4.2, GitHub Actions, Vercel CLI.

**Spec:** `docs/superpowers/specs/2026-09-15-cloud-hosting-design.md` — Build order item 2. Plan 2 of 5. Phase 3 (usage/analytics API) and phase 4 (cloud publish) flip the browser's feature flags on later.

## Prerequisites (human, before the named task)

- **Before Task 4:** in an **Administrator** terminal run `dotnet workload install wasm-tools-net8` (the SDK lives in Program Files, so a normal shell can't install workloads). Verify: `dotnet workload list` shows `wasm-tools-net8`.
- **Before Task 4 (download approval):** Task 4 downloads the OFL-licensed font `NotoSansHebrew[wdth,wght].ttf` (~100KB) from `https://github.com/google/fonts/raw/main/ofl/notosanshebrew/NotoSansHebrew%5Bwdth%2Cwght%5D.ttf`. The browser has no system fonts, and the bundled Inter font has no Hebrew glyphs, so Hebrew trip names/notes would render as boxes without it. The controller must get the user's explicit OK before dispatching Task 4.
- **Before Task 5 step "first deploy":** a Vercel account + project, and three GitHub repo secrets (entered by the user in GitHub → Settings → Secrets → Actions, never pasted into chat): `VERCEL_TOKEN`, `VERCEL_ORG_ID`, `VERCEL_PROJECT_ID`. Task 5 lists exactly where to find each.

## Global Constraints

- Branch: all work on `feature/cloud-hosting`, in the worktree `.worktrees/cloud-hosting`. Never commit to `main`.
- `net8.0` for every existing project; the browser host targets `net8.0-browser`. Avalonia packages pinned at `11.2.5`.
- Desktop behaviour and look must not change. Every desktop feature stays visible on desktop (`DesktopPlatformServices.Features` enables everything).
- `.github/workflows/release.yml`, `build/installer.iss`, `build/make-macos-dmg.sh` stay untouched.
- `GmapPlanner.Core` stays free of Playwright and Google.Apis (`CoreDependencyTests`). After Task 3, `GmapPlanner.UI` and `GmapPlanner.App.Browser` must not reference `GmapPlanner.Core.Publish`.
- C# namespaces of existing types do not change; new types: `GmapPlanner.App.Platform` (interface + desktop/browser implementations), `GmapPlanner.Core.Services` (Core helpers).
- **One interface only because two implementations exist now** (desktop + browser) — this is the case CLAUDE.md's "no single-implementation interfaces" rule allows. No DI container: hosts construct their implementation and pass it to `new MainViewModel(platform)`.
- Trimming rule #1: no reflection-based `System.Text.Json`; serialize through `GmapPlannerJsonContext`/`PublishJsonContext`. Rule #2: XAML keeps `x:CompileBindings="True"`.
- Every library that references Microsoft.Playwright (directly or transitively) sets `<PlaywrightPlatform>none</PlaywrightPlatform>`; the desktop exe keeps its RID mapping.
- **Deviation from spec (intentional):** the spec builds the WASM bundle on Vercel via `vercel.json` `installCommand`. That needs the `wasm-tools` workload (emscripten) inside Vercel's build image — slow and fragile. This plan builds in GitHub Actions and uploads the finished static folder with `vercel deploy`. Task 5 updates the spec.
- Browser Gemini calls send PDFs as `inline_data` (base64); browser upload cap is 14 MB (base64 of 15 MB exceeds Gemini's 20 MB inline request limit). Desktop keeps the Files API upload and the 15 MB cap.
- Every commit message ends with `Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>`.
- Close any running `GmapPlanner.App` before `dotnet build` (it locks its output).

## File Structure

| Path | Status | Responsibility |
|---|---|---|
| `src/GmapPlanner.Core/Services/Gemini/GeminiExtractionService.cs` | modify | bytes overload; `inlineFiles` option |
| `src/GmapPlanner.Core/Services/PipelineService.cs` | modify | `GenerateAsync(fileName, bytes, …)` overload |
| `tests/GmapPlanner.Core.Tests/GeminiInputTests.cs` | create | bytes + inline PDF tests |
| `tests/GmapPlanner.Core.Tests/PipelineServiceTests.cs` | modify | bytes overload test |
| `src/GmapPlanner.Core/Services/AppSettingsService.cs` | modify | `ToJson` / `FromJson` |
| `src/GmapPlanner.Core/Services/SetupBundleService.cs` | modify | pure `Merge` / `MergeFromText`; `Apply` on top |
| `src/GmapPlanner.Core/Services/AnalyticsSheet.cs` | create | `AnalyticsRow` + sheet-id helpers (moved) |
| `src/GmapPlanner.Core/Services/UsageGauge.cs` | create | `UsageGauge` record (moved) |
| `src/GmapPlanner.Core.Publish/Services/SheetsAnalyticsService.cs` | modify | use `AnalyticsSheet` |
| `src/GmapPlanner.Core.Publish/Services/UsageService.cs` | modify | drop moved record |
| `tests/GmapPlanner.Core.Tests/HostHelpersTests.cs` | create | settings JSON + bundle merge tests |
| `tests/GmapPlanner.Core.Tests/SheetsAnalyticsServiceTests.cs` | modify | call `AnalyticsSheet` |
| `src/GmapPlanner.UI/Platform/IPlatformServices.cs` | create | host seam + `PlatformFeatures`, `MapResult`, `SetupStatus` |
| `src/GmapPlanner.UI/ViewModels/MainViewModel.cs` | rewrite | host-agnostic VM |
| `src/GmapPlanner.UI/ViewModels/KmlFileItem.cs` | rewrite | wraps an in-memory `KmlFile` |
| `src/GmapPlanner.UI/Views/MainView.axaml(.cs)` | modify | feature visibility; storage files instead of paths |
| `src/GmapPlanner.UI/GmapPlanner.UI.csproj` | modify | drop Core.Publish reference + PlaywrightPlatform |
| `src/GmapPlanner.App/Platform/DesktopPlatformServices.cs` | create | desktop implementation |
| `src/GmapPlanner.App/App.axaml.cs`, `GmapPlanner.App.csproj` | modify | pass platform; reference Core.Publish |
| `src/GmapPlanner.App.Browser/**` | create | WASM host, `BrowserPlatformServices`, wwwroot, Hebrew font |
| `.github/workflows/web.yml` | create | build WASM + deploy to Vercel |
| `CLAUDE.md`, spec | modify | document the browser host + deploy |

---

### Task 1: Itinerary bytes input and inline PDFs in Core

**Files:**
- Modify: `src/GmapPlanner.Core/Services/Gemini/GeminiExtractionService.cs` (class declaration line 17; `ExtractItineraryAsync` lines 65–82; `BuildContentPartsAsync` lines 84–112; `UploadFileAsync` start, lines 198–216)
- Modify: `src/GmapPlanner.Core/Services/PipelineService.cs`
- Create: `tests/GmapPlanner.Core.Tests/GeminiInputTests.cs`
- Modify: `tests/GmapPlanner.Core.Tests/PipelineServiceTests.cs`

**Interfaces:**
- Consumes: existing `GeminiExtractionService`, `PipelineService.GenerateAsync(string filePath, …)`, `PipelineResult`, `KmlFile`.
- Produces:
  - `public class GeminiExtractionService(HttpClient http, string apiKey, string? promptOverride = null, bool inlineFiles = false)`
  - `public Task<Trip> ExtractItineraryAsync(string fileName, byte[] content, CancellationToken ct = default)` — `fileName` only selects the type (`.txt`/`.pdf`) and names the upload. `.txt` is decoded as UTF-8 with BOM detection. `.pdf` goes through the Files API, or as `inline_data` base64 when `inlineFiles` is true. Other extensions throw `PipelineException`.
  - `ExtractItineraryAsync(string filePath, …)` unchanged signature; reads the bytes then delegates.
  - `public Task<PipelineResult> PipelineService.GenerateAsync(string fileName, byte[] content, int layersPerFile = AppConfig.MaxLayersPerFile, bool noGeocode = false, ProgressCallback? progress = null, CancellationToken ct = default)`

- [ ] **Step 1: Write the failing Gemini input tests**

Create `tests/GmapPlanner.Core.Tests/GeminiInputTests.cs`:

```csharp
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using GmapPlanner.Core.Errors;
using GmapPlanner.Core.Services.Gemini;
using Xunit;

namespace GmapPlanner.Core.Tests;

/// <summary>
/// The browser host has bytes, not paths, and can't read the Files API's upload-URL header
/// cross-origin — so it extracts from bytes and sends PDFs inline.
/// </summary>
public class GeminiInputTests
{
    private const string Good = """{"trip_name": "Kyoto", "days": [{"day": 1, "date": "", "locations": []}]}""";

    [Fact]
    public async Task TxtBytes_AreSentAsText_WithBomStripped()
    {
        var handler = new RecordingHandler(Envelope(Good));
        var content = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("Day 1: קיוטו")).ToArray();

        var trip = await new GeminiExtractionService(new HttpClient(handler), apiKey: "k")
            .ExtractItineraryAsync("trip.txt", content);

        Assert.Equal("Kyoto", trip.TripName);
        var parts = JsonNode.Parse(Assert.Single(handler.Bodies))!["contents"]![0]!["parts"]!.AsArray();
        Assert.Equal("Day 1: קיוטו", parts[1]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task InlineFiles_SendsPdfAsBase64InlineData_WithoutUpload()
    {
        var handler = new RecordingHandler(Envelope(Good));

        await new GeminiExtractionService(new HttpClient(handler), apiKey: "k", inlineFiles: true)
            .ExtractItineraryAsync("trip.pdf", [1, 2, 3]);

        var uri = Assert.Single(handler.Uris);
        Assert.Contains(":generateContent", uri);
        var inline = JsonNode.Parse(handler.Bodies[0])!["contents"]![0]!["parts"]![1]!["inline_data"]!;
        Assert.Equal("application/pdf", inline["mime_type"]!.GetValue<string>());
        Assert.Equal("AQID", inline["data"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnsupportedExtension_Throws()
    {
        var handler = new RecordingHandler(Envelope(Good));

        var ex = await Assert.ThrowsAsync<PipelineException>(() =>
            new GeminiExtractionService(new HttpClient(handler), apiKey: "k", inlineFiles: true)
                .ExtractItineraryAsync("trip.docx", [1]));

        Assert.Contains("trip.docx", ex.Message);
        Assert.Empty(handler.Uris);
    }

    private static string Envelope(string text) => new JsonObject
    {
        ["candidates"] = new JsonArray(new JsonObject
        {
            ["content"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = text }) },
            ["finishReason"] = "STOP",
        }),
    }.ToJsonString();

    private sealed class RecordingHandler(string body) : HttpMessageHandler
    {
        public List<string> Uris { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Uris.Add(request.RequestUri!.ToString());
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/GmapPlanner.Core.Tests --filter "FullyQualifiedName~GeminiInputTests"`
Expected: build FAILS — `CS1739: The best overload for 'GeminiExtractionService' does not have a parameter named 'inlineFiles'` and/or `CS1503` on `ExtractItineraryAsync("trip.txt", content)`.

- [ ] **Step 3: Implement bytes input in `GeminiExtractionService`**

Replace the class declaration line:

```csharp
public class GeminiExtractionService(HttpClient http, string apiKey, string? promptOverride = null)
```

with:

```csharp
/// <param name="inlineFiles">
/// Send PDFs as base64 inline_data instead of the Files API upload. The browser host needs
/// this: the upload flow reads the X-Goog-Upload-URL response header, which a cross-origin
/// fetch can't see. Inline requests are capped at 20 MB by Gemini.
/// </param>
public class GeminiExtractionService(HttpClient http, string apiKey, string? promptOverride = null, bool inlineFiles = false)
```

Replace the whole `ExtractItineraryAsync(string filePath, …)` method and the whole `BuildContentPartsAsync` method (lines 65–112) with:

```csharp
    public async Task<Trip> ExtractItineraryAsync(string filePath, CancellationToken ct = default)
    {
        byte[] content;
        try
        {
            content = await File.ReadAllBytesAsync(filePath, ct);
        }
        catch (Exception e)
        {
            throw new PipelineException($"Failed to read '{filePath}': {e.Message}", e);
        }
        return await ExtractItineraryAsync(Path.GetFileName(filePath), content, ct);
    }

    /// <summary>
    /// Extracts from an itinerary already in memory. <paramref name="fileName"/> only picks the
    /// type (.txt / .pdf) and names the upload.
    /// </summary>
    public async Task<Trip> ExtractItineraryAsync(string fileName, byte[] content, CancellationToken ct = default)
    {
        var parts = await BuildContentPartsAsync(fileName, content, ct);

        PipelineException last = new("Gemini API (location extraction) was never called.");
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            try
            {
                return await ExtractOnceAsync(parts, ct);
            }
            catch (BadResponseException e)
            {
                last = e;
            }
        }
        throw last;
    }

    private async Task<List<JsonObject>> BuildContentPartsAsync(string fileName, byte[] content, CancellationToken ct)
    {
        var promptPart = new JsonObject { ["text"] = PromptText };

        if (Path.GetExtension(fileName).Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            // Same decoding File.ReadAllTextAsync did: UTF-8, honouring (and dropping) a BOM.
            using var reader = new StreamReader(new MemoryStream(content), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return [promptPart, new JsonObject { ["text"] = await reader.ReadToEndAsync(ct) }];
        }

        var mimeType = MimeTypeOf(fileName);
        if (inlineFiles)
        {
            return
            [
                promptPart,
                new JsonObject
                {
                    ["inline_data"] = new JsonObject { ["mime_type"] = mimeType, ["data"] = Convert.ToBase64String(content) },
                },
            ];
        }

        var (uri, uploadedMimeType) = await UploadFileAsync(fileName, mimeType, content, ct);
        return
        [
            promptPart,
            new JsonObject
            {
                ["file_data"] = new JsonObject { ["mime_type"] = uploadedMimeType, ["file_uri"] = uri },
            },
        ];
    }

    private static string MimeTypeOf(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf",
        _ => throw new PipelineException($"Unsupported file type for Gemini upload: '{fileName}' (expected .txt or .pdf)."),
    };
```

Replace the start of `UploadFileAsync`, from its signature through the `catch` that wraps `File.ReadAllBytesAsync`:

```csharp
    private async Task<(string Uri, string MimeType)> UploadFileAsync(string filePath, CancellationToken ct)
    {
        var mimeType = Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".pdf" => "application/pdf",
            _ => throw new PipelineException($"Unsupported file type for Gemini upload: '{filePath}' (expected .txt or .pdf)."),
        };
        var displayName = Path.GetFileName(filePath);
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(filePath, ct);
        }
        catch (Exception e)
        {
            throw new PipelineException($"Gemini Files API failed to upload '{displayName}': {e.Message}", e);
        }
```

with:

```csharp
    private async Task<(string Uri, string MimeType)> UploadFileAsync(string displayName, string mimeType, byte[] bytes, CancellationToken ct)
    {
```

(The rest of `UploadFileAsync` already uses `displayName`, `mimeType` and `bytes` — leave it unchanged.)

- [ ] **Step 4: Run the Gemini tests**

Run: `dotnet test tests/GmapPlanner.Core.Tests --filter "FullyQualifiedName~Gemini"`
Expected: PASS — the 3 new `GeminiInputTests` plus all existing `GeminiExtractionServiceTests`.

- [ ] **Step 5: Write the failing pipeline bytes test**

In `tests/GmapPlanner.Core.Tests/PipelineServiceTests.cs`, add this test after `GenerateAsync_ReturnsKmlInMemoryWithoutWritingFiles`:

```csharp
    [Fact]
    public async Task GenerateAsync_FromBytes_NeedsNoFileOnDisk()
    {
        var result = await Pipeline().GenerateAsync("itinerary.txt", "Day 1: Kyoto. Day 2: Tokyo."u8.ToArray(), layersPerFile: 10);

        Assert.Equal("Tokyo/Kyoto Trip", result.TripName);
        Assert.Equal("1-2.kml", Assert.Single(result.KmlFiles).FileName);
    }
```

Run: `dotnet test tests/GmapPlanner.Core.Tests --filter "FullyQualifiedName~PipelineServiceTests"`
Expected: build FAILS — `CS1503: Argument 2: cannot convert from 'byte[]' to 'int'`.

- [ ] **Step 6: Add the bytes overload to `PipelineService`**

In `src/GmapPlanner.Core/Services/PipelineService.cs`, replace the whole existing `GenerateAsync` method (its doc comment through its closing brace) with:

```csharp
    /// <summary>
    /// Extract -> geocode -> KML, held in memory (the browser host offers the files as
    /// downloads). Nothing is written to disk; Files and OutputDir stay empty.
    /// </summary>
    public Task<PipelineResult> GenerateAsync(
        string filePath,
        int layersPerFile = AppConfig.MaxLayersPerFile,
        bool noGeocode = false,
        ProgressCallback? progress = null,
        CancellationToken ct = default) =>
        BuildAsync(() => gemini.ExtractItineraryAsync(filePath, ct), layersPerFile, noGeocode, progress, ct);

    /// <summary>Same as the path overload, for an itinerary already in memory (the browser host).</summary>
    public Task<PipelineResult> GenerateAsync(
        string fileName,
        byte[] content,
        int layersPerFile = AppConfig.MaxLayersPerFile,
        bool noGeocode = false,
        ProgressCallback? progress = null,
        CancellationToken ct = default) =>
        BuildAsync(() => gemini.ExtractItineraryAsync(fileName, content, ct), layersPerFile, noGeocode, progress, ct);

    private async Task<PipelineResult> BuildAsync(
        Func<Task<Models.Trip>> extract, int layersPerFile, bool noGeocode, ProgressCallback? progress, CancellationToken ct)
    {
        progress?.Invoke("Extracting locations with Gemini", 0.35);
        var trip = await extract();
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
```

- [ ] **Step 7: Run the full suite and build**

Run: `dotnet test tests/GmapPlanner.Core.Tests`
Expected: 97 passed, 0 failed (93 + 3 Gemini input + 1 pipeline bytes).
Run: `dotnet build`
Expected: `Build succeeded.` `0 Error(s)`.

- [ ] **Step 8: Commit**

```bash
git add src/GmapPlanner.Core/Services/Gemini/GeminiExtractionService.cs src/GmapPlanner.Core/Services/PipelineService.cs tests/GmapPlanner.Core.Tests/GeminiInputTests.cs tests/GmapPlanner.Core.Tests/PipelineServiceTests.cs
git commit -m "Core: extract itineraries from bytes; optional inline PDFs for the browser host

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 2: Host-agnostic helpers in Core

**Files:**
- Modify: `src/GmapPlanner.Core/Services/AppSettingsService.cs`
- Modify: `src/GmapPlanner.Core/Services/SetupBundleService.cs`
- Create: `src/GmapPlanner.Core/Services/AnalyticsSheet.cs`
- Create: `src/GmapPlanner.Core/Services/UsageGauge.cs`
- Modify: `src/GmapPlanner.Core.Publish/Services/SheetsAnalyticsService.cs` (lines 8–9 record; lines 42–58 static helpers; every `SheetIdOf(` call)
- Modify: `src/GmapPlanner.Core.Publish/Services/UsageService.cs` (lines 10–11)
- Modify: `src/GmapPlanner.UI/ViewModels/MainViewModel.cs` (lines 139, 149, 216 — only the class name in the calls)
- Create: `tests/GmapPlanner.Core.Tests/HostHelpersTests.cs`
- Modify: `tests/GmapPlanner.Core.Tests/SheetsAnalyticsServiceTests.cs` (lines 15, 23)

**Interfaces:**
- Consumes: `AppSettings`, `GmapPlannerJsonContext` (internal to Core).
- Produces (all namespace `GmapPlanner.Core.Services`, assembly `GmapPlanner.Core`):
  - `public static string AppSettingsService.ToJson(AppSettings settings)`
  - `public static AppSettings AppSettingsService.FromJson(string? json)` — new `AppSettings()` on null, blank, or invalid JSON.
  - `public sealed record SetupBundleService.BundleMerge(AppSettings Settings, string? CredentialsJson, List<string> Applied)`
  - `public static BundleMerge SetupBundleService.Merge(JsonObject bundle, AppSettings current)` — pure; never mutates `current`; `CredentialsJson` is non-null only for valid JSON, and then `Applied` contains `"credentials.json"`.
  - `public static BundleMerge SetupBundleService.MergeFromText(string text, AppSettings current)` — throws `ArgumentException` for non-JSON / non-object, like `ApplyFromText`.
  - `SetupBundleService.Apply` / `ApplyFromText` unchanged signatures and behaviour (desktop).
  - `public static class AnalyticsSheet { IsConfigured(string saJson, string sheetId); SheetIdOf(string raw); SheetUrl(string sheetId); }` and `public sealed record AnalyticsRow(...)` (moved, same shape).
  - `public record UsageGauge(int Used, int Limit, double Percent, int? ResetDays)` (moved, same shape).

- [ ] **Step 1: Write the failing helper tests**

Create `tests/GmapPlanner.Core.Tests/HostHelpersTests.cs`:

```csharp
using System.Text.Json.Nodes;
using GmapPlanner.Core.Services;
using Xunit;

namespace GmapPlanner.Core.Tests;

/// <summary>Pure helpers both hosts share (the browser has no config.json and no credentials file).</summary>
public class HostHelpersTests
{
    [Fact]
    public void SettingsJson_RoundTrips()
    {
        var json = AppSettingsService.ToJson(new AppSettings { GoogleApiKey = "g", GeoApiKey = "p", AnalyticsSheetId = "s" });

        var loaded = AppSettingsService.FromJson(json);

        Assert.Equal("g", loaded.GoogleApiKey);
        Assert.Equal("p", loaded.GeoApiKey);
        Assert.Equal("s", loaded.AnalyticsSheetId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    public void SettingsJson_BadInputGivesDefaults(string? json) =>
        Assert.Equal("", AppSettingsService.FromJson(json).GoogleApiKey);

    [Fact]
    public void Merge_FillsPresentKeys_KeepsOthers_AndNeverMutatesCurrent()
    {
        var current = new AppSettings { GoogleApiKey = "old-gemini", GeoApiKey = "old-geo" };
        var bundle = new JsonObject
        {
            ["GOOGLE_API_KEY"] = "new-gemini",
            ["GEO_API_KEY"] = "  ",
            ["credentials"] = new JsonObject { ["installed"] = new JsonObject { ["client_id"] = "x" } },
        };

        var merged = SetupBundleService.Merge(bundle, current);

        Assert.Equal("new-gemini", merged.Settings.GoogleApiKey);
        Assert.Equal("old-geo", merged.Settings.GeoApiKey);
        Assert.Equal("old-gemini", current.GoogleApiKey);
        Assert.NotNull(merged.CredentialsJson);
        Assert.Equal("x", JsonNode.Parse(merged.CredentialsJson!)!["installed"]!["client_id"]!.GetValue<string>());
        Assert.Equal(new[] { "GOOGLE_API_KEY", "credentials.json" }, merged.Applied);
    }

    [Fact]
    public void Merge_SkipsCredentialsThatAreNotJson()
    {
        var merged = SetupBundleService.Merge(new JsonObject { ["credentials"] = "not json" }, new AppSettings());

        Assert.Null(merged.CredentialsJson);
        Assert.Empty(merged.Applied);
    }

    [Fact]
    public void MergeFromText_RejectsNonObjects() =>
        Assert.Throws<ArgumentException>(() => SetupBundleService.MergeFromText("[1,2,3]", new AppSettings()));
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/GmapPlanner.Core.Tests --filter "FullyQualifiedName~HostHelpersTests"`
Expected: build FAILS — `CS0117: 'AppSettingsService' does not contain a definition for 'ToJson'` and `'SetupBundleService' does not contain a definition for 'Merge'`.

- [ ] **Step 3: Add `ToJson` / `FromJson`**

In `src/GmapPlanner.Core/Services/AppSettingsService.cs`, replace the whole `AppSettingsService` class with:

```csharp
public static class AppSettingsService
{
    private static string ConfigPath => AppDataPaths.DataPath("config.json");

    public static AppSettings Load()
    {
        try
        {
            return FromJson(File.ReadAllText(ConfigPath));
        }
        catch
        {
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings) => File.WriteAllText(ConfigPath, ToJson(settings));

    /// <summary>Settings as JSON (source-generated; the browser host keeps this in localStorage).</summary>
    public static string ToJson(AppSettings settings) =>
        JsonSerializer.Serialize(settings, GmapPlannerJsonContext.Default.AppSettings);

    /// <summary>Parses settings JSON; null, blank or invalid JSON gives defaults.</summary>
    public static AppSettings FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new AppSettings();
        try
        {
            return JsonSerializer.Deserialize(json, GmapPlannerJsonContext.Default.AppSettings) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }
}
```

- [ ] **Step 4: Split `SetupBundleService` into pure merge + desktop apply**

In `src/GmapPlanner.Core/Services/SetupBundleService.cs`, replace everything from `public record BundleResult(` through the end of `ApplyFromText` (keep `TryText` and `AsText` below it unchanged) with:

```csharp
    public record BundleResult(List<string> Applied)
    {
        public bool AnythingApplied => Applied.Count > 0;
    }

    /// <summary>The outcome of merging a bundle into settings, without touching disk.</summary>
    public sealed record BundleMerge(AppSettings Settings, string? CredentialsJson, List<string> Applied);

    /// <summary>
    /// Merges a bundle into a copy of <paramref name="current"/>. Pure: the caller decides where
    /// the settings and credentials.json go (a file on desktop, the browser's storage).
    /// </summary>
    public static BundleMerge Merge(JsonObject bundle, AppSettings current)
    {
        var settings = current with { };
        var applied = new List<string>();

        if (TryText(bundle, "GOOGLE_API_KEY", out var gemini)) { settings.GoogleApiKey = gemini; applied.Add("GOOGLE_API_KEY"); }
        if (TryText(bundle, "GEO_API_KEY", out var geo)) { settings.GeoApiKey = geo; applied.Add("GEO_API_KEY"); }
        if (TryText(bundle, "GCP_SA_JSON", out var sa)) { settings.GcpSaJson = sa; applied.Add("GCP_SA_JSON"); }
        if (TryText(bundle, "ANALYTICS_SHEET_ID", out var sheet)) { settings.AnalyticsSheetId = sheet; applied.Add("ANALYTICS_SHEET_ID"); }

        // Drive OAuth client credentials.json is a file, not a config key.
        string? credentials = null;
        foreach (var alias in CredentialAliases)
        {
            if (!bundle.TryGetPropertyValue(alias, out var node) || node is null) continue;
            var text = AsText(node);
            if (string.IsNullOrWhiteSpace(text)) continue;
            try
            {
                JsonNode.Parse(text); // only real JSON counts
                credentials = text;
                applied.Add("credentials.json");
            }
            catch
            {
                // Not valid JSON — skip rather than hand back a corrupt file.
            }
            break;
        }

        return new BundleMerge(settings, credentials, applied);
    }

    /// <summary>Parses bundle text and merges it. Throws on invalid JSON (not a JSON object).</summary>
    public static BundleMerge MergeFromText(string text, AppSettings current) => Merge(ParseBundle(text), current);

    /// <summary>Desktop: merges into config.json and writes credentials.json. Returns the fields actually written.</summary>
    public static BundleResult Apply(JsonObject bundle)
    {
        var merged = Merge(bundle, AppSettingsService.Load());
        AppSettingsService.Save(merged.Settings);

        var applied = merged.Applied;
        if (merged.CredentialsJson is not null)
        {
            try
            {
                File.WriteAllText(AppConfig.DriveCredentialsFile, merged.CredentialsJson);
            }
            catch
            {
                applied.Remove("credentials.json"); // not written, so not applied
            }
        }
        return new BundleResult(applied);
    }

    /// <summary>Parses bundle text and applies it. Throws on invalid JSON (not a JSON object).</summary>
    public static BundleResult ApplyFromText(string text) => Apply(ParseBundle(text));

    private static JsonObject ParseBundle(string text)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException e)
        {
            throw new ArgumentException($"Not a valid setup file: {e.Message}", e);
        }
        return node as JsonObject ?? throw new ArgumentException("The setup file must contain a JSON object.");
    }
```

- [ ] **Step 5: Move the analytics/usage records and sheet helpers into Core**

Create `src/GmapPlanner.Core/Services/AnalyticsSheet.cs`:

```csharp
namespace GmapPlanner.Core.Services;

/// <summary>One logged trip, read back from the analytics Sheet.</summary>
public sealed record AnalyticsRow(string CreatedAt, string TripName, int Maps, int Places, IReadOnlyList<string> MapLinks);

/// <summary>Analytics Sheet id handling shared by every host (no Google.Apis needed).</summary>
public static class AnalyticsSheet
{
    /// <summary>True when both the service account and a Sheet id are configured.</summary>
    public static bool IsConfigured(string saJson, string sheetId) =>
        !string.IsNullOrWhiteSpace(saJson) && !string.IsNullOrWhiteSpace(SheetIdOf(sheetId));

    /// <summary>The Sheet id, accepting either a bare id or a full spreadsheet URL.</summary>
    public static string SheetIdOf(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        raw = raw.Trim();
        const string marker = "/spreadsheets/d/";
        var i = raw.IndexOf(marker, StringComparison.Ordinal);
        return i < 0 ? raw : raw[(i + marker.Length)..].Split('/', 2)[0];
    }

    /// <summary>The shareable URL for a Sheet id (for the "view source" link).</summary>
    public static string SheetUrl(string sheetId) =>
        $"https://docs.google.com/spreadsheets/d/{SheetIdOf(sheetId)}";
}
```

Create `src/GmapPlanner.Core/Services/UsageGauge.cs`:

```csharp
namespace GmapPlanner.Core.Services;

/// <summary>One API's usage as a percent-of-quota gauge.</summary>
public record UsageGauge(int Used, int Limit, double Percent, int? ResetDays);
```

In `src/GmapPlanner.Core.Publish/Services/UsageService.cs`, delete these two lines (the record now lives in Core):

```csharp
/// <summary>One API's usage as a percent-of-quota gauge.</summary>
public record UsageGauge(int Used, int Limit, double Percent, int? ResetDays);
```

In `src/GmapPlanner.Core.Publish/Services/SheetsAnalyticsService.cs`:
1. Delete the two lines `/// <summary>One logged trip, read back from the analytics Sheet.</summary>` and `public sealed record AnalyticsRow(...)`.
2. Delete the three static members `IsConfigured`, `SheetIdOf`, `SheetUrl` together with their `/// <summary>` lines (the block between `private static string KeyCol(...)` and `// --- Public API`).
3. Replace every remaining call `SheetIdOf(` in the file with `AnalyticsSheet.SheetIdOf(`. Check: `grep -n "SheetIdOf(" src/GmapPlanner.Core.Publish/Services/SheetsAnalyticsService.cs` — every hit must read `AnalyticsSheet.SheetIdOf(`.

In `tests/GmapPlanner.Core.Tests/SheetsAnalyticsServiceTests.cs`, replace `SheetsAnalyticsService.SheetIdOf(raw)` with `AnalyticsSheet.SheetIdOf(raw)` and `SheetsAnalyticsService.IsConfigured(sa, sheet)` with `AnalyticsSheet.IsConfigured(sa, sheet)`.

In `src/GmapPlanner.UI/ViewModels/MainViewModel.cs`, replace the three calls `SheetsAnalyticsService.IsConfigured(` (two places) and `SheetsAnalyticsService.SheetUrl(` (one place) with `AnalyticsSheet.IsConfigured(` and `AnalyticsSheet.SheetUrl(`. (Task 3 rewrites this file; this keeps the build green now.)

- [ ] **Step 6: Run all tests and build**

Run: `dotnet test tests/GmapPlanner.Core.Tests`
Expected: 104 passed, 0 failed (97 + 7 new: 1 round-trip, 3 bad-input theory cases, 3 merge tests).
Run: `dotnet build`
Expected: `Build succeeded.` `0 Error(s)`.

- [ ] **Step 7: Commit**

```bash
git add src/GmapPlanner.Core src/GmapPlanner.Core.Publish/Services/SheetsAnalyticsService.cs src/GmapPlanner.Core.Publish/Services/UsageService.cs src/GmapPlanner.UI/ViewModels/MainViewModel.cs tests/GmapPlanner.Core.Tests/HostHelpersTests.cs tests/GmapPlanner.Core.Tests/SheetsAnalyticsServiceTests.cs
git status --short
git commit -m "Core: host-agnostic settings JSON, setup-bundle merge, analytics/usage records

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 3: Platform seam — `IPlatformServices` with the desktop implementation

**Files:**
- Create: `src/GmapPlanner.UI/Platform/IPlatformServices.cs`
- Create: `src/GmapPlanner.App/Platform/DesktopPlatformServices.cs`
- Rewrite: `src/GmapPlanner.UI/ViewModels/KmlFileItem.cs`
- Rewrite: `src/GmapPlanner.UI/ViewModels/MainViewModel.cs`
- Rewrite: `src/GmapPlanner.UI/Views/MainView.axaml.cs`
- Modify: `src/GmapPlanner.UI/Views/MainView.axaml` (Design.DataContext; feature visibility)
- Modify: `src/GmapPlanner.UI/GmapPlanner.UI.csproj`, `src/GmapPlanner.App/GmapPlanner.App.csproj`, `src/GmapPlanner.App/App.axaml.cs`

**Interfaces:**
- Consumes (Task 1): `GeminiExtractionService(HttpClient, string apiKey, string? promptOverride = null, bool inlineFiles = false)`, `PipelineService.GenerateAsync(string fileName, byte[] content, int layersPerFile, bool noGeocode, ProgressCallback? progress, CancellationToken ct)`.
- Consumes (Task 2): `AppSettingsService.Load/Save`, `SetupBundleService.MergeFromText(string, AppSettings) → BundleMerge(Settings, CredentialsJson, Applied)`, `AnalyticsSheet.IsConfigured/SheetUrl`, `AnalyticsRow`, `UsageGauge`.
- Consumes (phase 1): `KmlFile(FileName, Content)`, `KmlBuilder.SaveKmlFiles(IEnumerable<KmlFile>, string) → List<string>`, `KmlBuilder.SanitizeFolderName`, `PublishService.PublishKmlFilesAsync`, `PublishedMap(File, ViewUrl, SharedWith, Error)`, `MyMapsSession.LoginAsync()`, `MyMapsSession.StartAsync(headless: true)` + `IsLoggedInAsync()`, `UsageService.GetGeocodeUsageAsync(string)`, `SheetsAnalyticsService.FetchRowsAsync / RecordPublishAsync`, `UpdateService`.
- Produces (namespace `GmapPlanner.App.Platform`, assembly `GmapPlanner.UI`):
  - `public sealed record PlatformFeatures(bool Publish, bool Analytics, bool Updates, bool InlineFiles, int MaxUploadMb)`
  - `public sealed record MapResult(string FileName, string ViewUrl, IReadOnlyList<string> SharedWith, string Error)`
  - `public sealed record SetupStatus(bool HasGoogleLogin, bool HasDriveCredentials, bool HasDriveToken)`
  - `public interface IPlatformServices` — members exactly as in Step 1.
  - `public MainViewModel(IPlatformServices platform)`; `public PlatformFeatures Features`; `public Task LoadInputFileAsync(IStorageFile file)`; `public Task LoadSetupBundleAsync(IStorageFile file)`; `public Task LoadDriveCredentialsAsync(IStorageFile file)`; `public Task SaveKmlFilesAsync(IStorageProvider storage)`.
  - `KmlFileItem.From(KmlFile file)`; `KmlFileItem.File` (`KmlFile`).
- Produces (namespace `GmapPlanner.App.Platform`, assembly `GmapPlanner.App`): `public sealed class DesktopPlatformServices : IPlatformServices` with `Features = new(Publish: true, Analytics: true, Updates: true, InlineFiles: false, MaxUploadMb: 15)`.

This task has no unit tests of its own: `GmapPlanner.UI` has no test project, and the view model change is wiring over services that already have tests. Its gates are a clean build, `GmapPlanner.UI` no longer referencing Core.Publish, the Core suite still green, and the desktop smoke test in Step 10.

- [ ] **Step 1: Create the platform seam**

Create `src/GmapPlanner.UI/Platform/IPlatformServices.cs`:

```csharp
using Avalonia.Platform.Storage;
using GmapPlanner.Core.Services;

namespace GmapPlanner.App.Platform;

/// <summary>What a host can do. The view hides everything a host can't.</summary>
/// <param name="Publish">My Maps publishing, Google login, Drive credentials.</param>
/// <param name="Analytics">Usage ring + Analytics Sheet (both need the service-account JSON).</param>
/// <param name="Updates">The in-app updater card.</param>
/// <param name="InlineFiles">Send PDFs to Gemini inline (the browser can't use the upload flow).</param>
/// <param name="MaxUploadMb">Largest itinerary accepted.</param>
public sealed record PlatformFeatures(bool Publish, bool Analytics, bool Updates, bool InlineFiles, int MaxUploadMb);

/// <summary>One published map, host-agnostic.</summary>
public sealed record MapResult(string FileName, string ViewUrl, IReadOnlyList<string> SharedWith, string Error);

/// <summary>The login/Drive rows of the Settings checklist.</summary>
public sealed record SetupStatus(bool HasGoogleLogin, bool HasDriveCredentials, bool HasDriveToken);

/// <summary>
/// Everything host-specific the view model needs. Two implementations: the desktop exe
/// (files, Playwright, Drive, Sheets, Monitoring, updater) and the browser (localStorage,
/// downloads; phases 3–4 fill in analytics and publishing through the Vercel API).
/// </summary>
public interface IPlatformServices
{
    PlatformFeatures Features { get; }

    AppSettings LoadSettings();
    void SaveSettings(AppSettings settings);
    SetupStatus GetSetupStatus();
    void SaveDriveCredentials(string json);

    void OpenUrl(string url);

    /// <summary>Hands the KML files to the user. Returns a status line, or "" if the user cancelled.</summary>
    Task<string> SaveKmlFilesAsync(IStorageProvider storage, IReadOnlyList<KmlFile> files);

    Task<UsageGauge?> GetUsageAsync(string serviceAccountJson);
    Task<IReadOnlyList<AnalyticsRow>?> FetchAnalyticsAsync(string serviceAccountJson, string sheetId);
    Task RecordTripAsync(string serviceAccountJson, string sheetId, string tripName, int maps, int places, IReadOnlyList<string> mapLinks);

    /// <summary>One map per KML file. Throws for a setup/auth failure; per-file failures come back in <see cref="MapResult.Error"/>.</summary>
    Task<IReadOnlyList<MapResult>> PublishAsync(
        string tripName, IReadOnlyList<KmlFile> files, IReadOnlyList<string> recipients,
        string role, bool notify, bool showBrowser, ProgressCallback progress);

    Task LoginAsync();
    Task<bool> IsLoggedInAsync();

    Task<UpdateInfo?> CheckForUpdateAsync();

    /// <summary>Downloads and starts the installer (on Windows this exits the app).</summary>
    Task InstallUpdateAsync(UpdateInfo update, Action<double> progress);
}
```

- [ ] **Step 2: Create the desktop implementation**

Create `src/GmapPlanner.App/Platform/DesktopPlatformServices.cs`:

```csharp
using System.Diagnostics;
using Avalonia.Platform.Storage;
using GmapPlanner.Core;
using GmapPlanner.Core.Services;
using GmapPlanner.Core.Services.Publish;

namespace GmapPlanner.App.Platform;

/// <summary>The desktop host: config.json, a real browser for My Maps, Drive/Sheets/Monitoring, the updater.</summary>
public sealed class DesktopPlatformServices : IPlatformServices
{
    private static readonly HttpClient Http = new();
    private readonly UsageService _usage = new(Http);
    private readonly SheetsAnalyticsService _sheets = new(Http);
    private readonly UpdateService _updater = new(Http);

    // KML handed to Playwright is written here, never the user's Downloads.
    // ponytail: OS temp, no cleanup — files are small and Windows/macOS reclaim temp.
    private static string WorkDir => Path.Combine(Path.GetTempPath(), "GmapPlanner");

    public PlatformFeatures Features { get; } = new(Publish: true, Analytics: true, Updates: true, InlineFiles: false, MaxUploadMb: 15);

    public AppSettings LoadSettings() => AppSettingsService.Load();

    public void SaveSettings(AppSettings settings) => AppSettingsService.Save(settings);

    public SetupStatus GetSetupStatus() => new(
        DirHasFiles(AppConfig.PlaywrightProfileDir),
        File.Exists(AppConfig.DriveCredentialsFile),
        DirHasFiles(AppConfig.DriveTokenDir));

    public void SaveDriveCredentials(string json) => File.WriteAllText(AppConfig.DriveCredentialsFile, json);

    public void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* opening a browser is a nicety */ }
    }

    public async Task<string> SaveKmlFilesAsync(IStorageProvider storage, IReadOnlyList<KmlFile> files)
    {
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Save KML files to…",
            AllowMultiple = false,
        });
        var folder = folders.FirstOrDefault()?.TryGetLocalPath();
        if (folder is null) return "";

        KmlBuilder.SaveKmlFiles(files, folder);
        try
        {
            // Best-effort reveal, like the Python app's reveal_in_file_manager.
            var (exe, args) = OperatingSystem.IsMacOS() ? ("open", folder) : ("explorer.exe", folder);
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true });
        }
        catch
        {
            // Opening a file manager is a nicety; never fail over it.
        }
        return $"Saved {files.Count} file(s) to {folder}";
    }

    public Task<UsageGauge?> GetUsageAsync(string serviceAccountJson) => _usage.GetGeocodeUsageAsync(serviceAccountJson);

    public async Task<IReadOnlyList<AnalyticsRow>?> FetchAnalyticsAsync(string serviceAccountJson, string sheetId) =>
        await _sheets.FetchRowsAsync(serviceAccountJson, sheetId);

    public Task RecordTripAsync(string serviceAccountJson, string sheetId, string tripName, int maps, int places, IReadOnlyList<string> mapLinks) =>
        _sheets.RecordPublishAsync(serviceAccountJson, sheetId, tripName, maps, places, mapLinks);

    public async Task<IReadOnlyList<MapResult>> PublishAsync(
        string tripName, IReadOnlyList<KmlFile> files, IReadOnlyList<string> recipients,
        string role, bool notify, bool showBrowser, ProgressCallback progress)
    {
        // Playwright imports from disk, so the in-memory KML is written out first.
        var paths = KmlBuilder.SaveKmlFiles(files, Path.Combine(WorkDir, KmlBuilder.SanitizeFolderName(tripName)));
        var maps = await PublishService.PublishKmlFilesAsync(
            paths, tripName, recipients, role: role, headless: !showBrowser, notify: notify, progress: progress);
        return maps.Select(m => new MapResult(Path.GetFileName(m.File), m.ViewUrl, m.SharedWith, m.Error)).ToList();
    }

    public Task LoginAsync() => MyMapsSession.LoginAsync();

    public async Task<bool> IsLoggedInAsync()
    {
        await using var session = await MyMapsSession.StartAsync(headless: true);
        return await session.IsLoggedInAsync();
    }

    public Task<UpdateInfo?> CheckForUpdateAsync() => _updater.CheckForUpdateAsync(AppConfig.GithubRepo);

    public async Task InstallUpdateAsync(UpdateInfo update, Action<double> progress)
    {
        var path = await _updater.DownloadAssetAsync(update.AssetUrl, update.AssetName, progress: progress);
        // On Windows this quits the app so the installer can replace the files, then
        // relaunches the new version; on macOS it opens the .dmg for a drag-install.
        UpdateService.ApplyUpdate(path);
    }

    private static bool DirHasFiles(string path)
    {
        try { return Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any(); }
        catch { return false; }
    }
}
```

`SheetsAnalyticsService.RecordPublishAsync` takes `IEnumerable<string>` for its links, and `IReadOnlyList<string>` converts to it implicitly. If the build reports a signature mismatch on any wrapped call, match the real parameter types with `grep -n "public async Task RecordPublishAsync" -A3 src/GmapPlanner.Core.Publish/Services/SheetsAnalyticsService.cs`. Don't change what these calls do.

- [ ] **Step 3: Make `KmlFileItem` wrap an in-memory file**

Replace the entire contents of `src/GmapPlanner.UI/ViewModels/KmlFileItem.cs` with:

```csharp
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using GmapPlanner.Core.Services;

namespace GmapPlanner.App.ViewModels;

/// <summary>
/// One generated KML file in the results list. Publishing fills in the map fields
/// afterwards, so this is observable rather than a plain record.
/// </summary>
public partial class KmlFileItem : ObservableObject
{
    public required KmlFile File { get; init; }
    public required string DayLabel { get; init; }
    public required string SizeText { get; init; }
    public string FileName => File.FileName;

    [ObservableProperty] private string _mapUrl = "";
    [ObservableProperty] private string _sharedWith = "";
    [ObservableProperty] private string _mapError = "";

    public bool HasMap => MapUrl.Length > 0;
    partial void OnMapUrlChanged(string value) => OnPropertyChanged(nameof(HasMap));

    public static KmlFileItem From(KmlFile file)
    {
        var label = Path.GetFileNameWithoutExtension(file.FileName);
        return new KmlFileItem
        {
            File = file,
            DayLabel = label.Contains('-') ? $"Days {label}" : $"Day {label}",
            SizeText = $"{Encoding.UTF8.GetByteCount(file.Content) / 1024.0:F0} KB",
        };
    }
}
```

- [ ] **Step 4: Rewrite `MainViewModel` over the seam**

Replace the entire contents of `src/GmapPlanner.UI/ViewModels/MainViewModel.cs` with the file below. Behaviour matches the previous view model: the same messages, the same order of work, and the same best-effort handling. What changed:
- Host-specific calls go through `_platform`.
- Input arrives as bytes from an `IStorageFile`.
- Generation uses `GenerateAsync` in memory.
- Publish rows are matched by file name.

```csharp
using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GmapPlanner.App.Platform;
using GmapPlanner.Core;
using GmapPlanner.Core.Services;
using GmapPlanner.Core.Services.Gemini;

namespace GmapPlanner.App.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private static readonly HttpClient Http = new();

    private readonly IPlatformServices _platform;

    public int MaxLayersPerFile => AppConfig.MaxLayersPerFile;

    /// <summary>What this host supports; the view binds visibility to it.</summary>
    public PlatformFeatures Features => _platform.Features;

    // --- Navigation ---------------------------------------------------------
    public enum AppPage { MakeMap, Analytics, Settings }

    [ObservableProperty] private AppPage _page = AppPage.MakeMap;
    public bool IsMakeMapPage => Page == AppPage.MakeMap;
    public bool IsAnalyticsPage => Page == AppPage.Analytics;
    public bool IsSettingsPage => Page == AppPage.Settings;

    partial void OnPageChanged(AppPage value)
    {
        OnPropertyChanged(nameof(IsMakeMapPage));
        OnPropertyChanged(nameof(IsAnalyticsPage));
        OnPropertyChanged(nameof(IsSettingsPage));
        if (value == AppPage.Analytics) _ = LoadAnalyticsAsync();
    }

    // --- Input + settings ---------------------------------------------------
    private byte[]? _inputContent;
    [ObservableProperty] private string _inputFileName = "";
    [ObservableProperty] private string _googleApiKey;
    [ObservableProperty] private string _geoApiKey;
    [ObservableProperty] private string _gcpSaJson;
    [ObservableProperty] private string _analyticsSheetId;
    [ObservableProperty] private string _setupMessage = "";

    // --- Setup status (green/⚪ checklist) -----------------------------------
    [ObservableProperty] private bool _hasGeminiKey;
    [ObservableProperty] private bool _hasGeoKey;
    [ObservableProperty] private bool _hasGoogleLogin;
    [ObservableProperty] private bool _hasDriveCredentials;
    [ObservableProperty] private bool _hasDriveToken;

    // --- Usage gauge --------------------------------------------------------
    private bool _usageLoading;
    [ObservableProperty] private bool _hasUsage;
    [ObservableProperty] private Geometry? _usageRingGeometry;
    [ObservableProperty] private string _usageColor = "#388E3C";
    [ObservableProperty] private string _usagePercentText = "";
    [ObservableProperty] private string _usageSubText = "";

    // --- Options (the Streamlit sidebar) ------------------------------------
    [ObservableProperty] private int _layersPerFile = AppConfig.MaxLayersPerFile;
    [ObservableProperty] private bool _skipGeocoding;

    // --- Publish to My Maps -------------------------------------------------
    [ObservableProperty] private bool _publishEnabled;
    [ObservableProperty] private int _shareRoleIndex; // 0 viewer, 1 commenter, 2 editor
    [ObservableProperty] private bool _notifyShare = true;
    [ObservableProperty] private bool _showBrowser;
    [ObservableProperty] private string _loginStatus = "";
    [ObservableProperty] private bool _isLoggingIn;

    public string[] ShareRoles { get; } = ["viewer", "commenter", "editor"];

    private string SelectedRole =>
        ShareRoles[Math.Clamp(ShareRoleIndex, 0, ShareRoles.Length - 1)];

    /// <summary>Recipient email chips shown in the token input. Deduped on add.</summary>
    public ObservableCollection<string> ShareEmailsList { get; } = [];

    private static readonly char[] EmailSeparators = [',', ';', ' ', '\t', '\n', '\r'];

    /// <summary>Splits a typed/pasted string on the usual separators and adds each as a chip.</summary>
    public void AddEmails(string raw)
    {
        foreach (var email in raw.Split(EmailSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!ShareEmailsList.Any(x => string.Equals(x, email, StringComparison.OrdinalIgnoreCase)))
                ShareEmailsList.Add(email);
    }

    /// <summary>Backspace on an empty input pops the last chip.</summary>
    public void RemoveLastEmail()
    {
        if (ShareEmailsList.Count > 0) ShareEmailsList.RemoveAt(ShareEmailsList.Count - 1);
    }

    [RelayCommand]
    private void RemoveEmail(string? email)
    {
        if (email is not null) ShareEmailsList.Remove(email);
    }

    // --- Updates ------------------------------------------------------------
    private UpdateInfo? _pendingUpdate;

    public string AppVersion => $"v{UpdateService.CurrentVersion()}";
    [ObservableProperty] private string _updateStatus = "";
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private bool _isCheckingUpdate;

    partial void OnIsCheckingUpdateChanged(bool value)
    {
        CheckForUpdatesCommand.NotifyCanExecuteChanged();
        DownloadAndInstallUpdateCommand.NotifyCanExecuteChanged();
    }

    // --- Analytics (Google Sheet) -------------------------------------------
    [ObservableProperty] private bool _hasAnalytics;              // rows loaded and shown
    [ObservableProperty] private bool _analyticsLoading;
    [ObservableProperty] private string _analyticsMessage = "";   // empty-state / config / error text
    [ObservableProperty] private string _totalTrips = "0";
    [ObservableProperty] private string _totalMaps = "0";
    [ObservableProperty] private string _totalPlaces = "0";
    [ObservableProperty] private string _analyticsThisMonth = "";

    public bool HasAnalyticsSheetLink => AnalyticsSheet.IsConfigured(GcpSaJson, AnalyticsSheetId);
    public ObservableCollection<AnalyticsBar> AnalyticsBars { get; } = [];

    /// <summary>
    /// Loads the analytics page from the Google Sheet. Best-effort: an unconfigured or
    /// unreachable Sheet shows a guidance message rather than an error. Called on nav + after a run.
    /// </summary>
    private async Task LoadAnalyticsAsync()
    {
        OnPropertyChanged(nameof(HasAnalyticsSheetLink));
        if (!AnalyticsSheet.IsConfigured(GcpSaJson, AnalyticsSheetId))
        {
            HasAnalytics = false;
            AnalyticsMessage = "Analytics storage isn't configured. On the Settings page, paste the "
                + "service-account JSON and set the Analytics Sheet ID, then share the Sheet (Editor) "
                + "with the service account's email and enable the Google Sheets API.";
            return;
        }

        AnalyticsLoading = true;
        AnalyticsMessage = "Loading from the Google Sheet…";
        try
        {
            var rows = await _platform.FetchAnalyticsAsync(GcpSaJson, AnalyticsSheetId);
            if (rows is null)
            {
                HasAnalytics = false;
                AnalyticsMessage = "Couldn't read the Sheet — check that it's shared with the service "
                    + "account and the Google Sheets API is enabled.";
                return;
            }
            if (rows.Count == 0)
            {
                HasAnalytics = false;
                AnalyticsMessage = "No trips logged yet. Generate a map and it'll show up here.";
                return;
            }

            TotalTrips = rows.Select(r => r.TripName).Distinct(StringComparer.OrdinalIgnoreCase).Count().ToString();
            TotalMaps = rows.Sum(r => r.Maps).ToString();
            TotalPlaces = rows.Sum(r => r.Places).ToString();

            var monthPrefix = DateTime.Now.ToString("yyyy-MM");
            var monthRows = rows.Where(r => r.CreatedAt.StartsWith(monthPrefix)).ToList();
            AnalyticsThisMonth = $"This month: {monthRows.Count} run(s), "
                + $"{monthRows.Sum(r => r.Maps)} map(s), {monthRows.Sum(r => r.Places)} place(s).";

            // Bar chart: places per trip for the most recent rows (newest at top).
            AnalyticsBars.Clear();
            var recent = rows.AsEnumerable().Reverse().Take(8).ToList();
            var max = Math.Max(1, recent.Max(r => r.Places));
            foreach (var r in recent)
                AnalyticsBars.Add(new AnalyticsBar
                {
                    Label = string.IsNullOrWhiteSpace(r.TripName) ? r.CreatedAt : r.TripName,
                    ValueText = r.Places.ToString(),
                    BarWidth = 20 + 240.0 * r.Places / max, // min stub so tiny values stay visible
                });

            HasAnalytics = true;
        }
        catch
        {
            HasAnalytics = false;
            AnalyticsMessage = "Couldn't load analytics right now.";
        }
        finally
        {
            AnalyticsLoading = false;
        }
    }

    [RelayCommand]
    private void OpenAnalyticsSheet()
    {
        if (HasAnalyticsSheetLink) _platform.OpenUrl(AnalyticsSheet.SheetUrl(AnalyticsSheetId));
    }

    // --- Run state ----------------------------------------------------------
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _errorText = "";
    [ObservableProperty] private bool _hasResult;
    [ObservableProperty] private string _resultTripName = "";
    [ObservableProperty] private string _resultDays = "";
    [ObservableProperty] private string _resultLocations = "";
    [ObservableProperty] private string _resultExactCoords = "";
    [ObservableProperty] private string _geocodeWarning = "";

    public ObservableCollection<KmlFileItem> ResultFiles { get; } = [];

    public MainViewModel(IPlatformServices platform)
    {
        _platform = platform;
        var settings = platform.LoadSettings();
        _googleApiKey = settings.GoogleApiKey;
        _geoApiKey = settings.GeoApiKey;
        _gcpSaJson = settings.GcpSaJson;
        _analyticsSheetId = settings.AnalyticsSheetId;
        RefreshSetupStatus();
    }

    partial void OnGoogleApiKeyChanged(string value) => SaveSettings();
    partial void OnGeoApiKeyChanged(string value) => SaveSettings();
    partial void OnGcpSaJsonChanged(string value) => SaveSettings();
    partial void OnAnalyticsSheetIdChanged(string value) => SaveSettings();
    partial void OnIsBusyChanged(bool value) => GenerateCommand.NotifyCanExecuteChanged();
    partial void OnInputFileNameChanged(string value) => GenerateCommand.NotifyCanExecuteChanged();

    private AppSettings CurrentSettings() => new()
    {
        GoogleApiKey = GoogleApiKey,
        GeoApiKey = GeoApiKey,
        GcpSaJson = GcpSaJson,
        AnalyticsSheetId = AnalyticsSheetId,
    };

    private void SaveSettings()
    {
        _platform.SaveSettings(CurrentSettings());
        RefreshSetupStatus();
    }

    /// <summary>Recomputes the green/⚪ checklist from the saved keys and the host's login/Drive state.</summary>
    private void RefreshSetupStatus()
    {
        HasGeminiKey = !string.IsNullOrWhiteSpace(GoogleApiKey);
        HasGeoKey = !string.IsNullOrWhiteSpace(GeoApiKey);
        var status = _platform.GetSetupStatus();
        HasGoogleLogin = status.HasGoogleLogin;
        HasDriveCredentials = status.HasDriveCredentials;
        HasDriveToken = status.HasDriveToken;
    }

    /// <summary>
    /// Applies a one-file setup JSON: fills the key fields and, if it carries a
    /// `credentials` object and the host can publish, stores credentials.json.
    /// </summary>
    public async Task LoadSetupBundleAsync(IStorageFile file)
    {
        try
        {
            var merged = SetupBundleService.MergeFromText(await ReadTextAsync(file), CurrentSettings());
            var applied = merged.Applied;
            if (merged.CredentialsJson is not null)
            {
                if (Features.Publish) _platform.SaveDriveCredentials(merged.CredentialsJson);
                else applied.Remove("credentials.json");
            }
            if (applied.Count == 0)
            {
                SetupMessage = "Nothing loaded — no recognized keys in that file.";
                return;
            }

            GoogleApiKey = merged.Settings.GoogleApiKey;
            GeoApiKey = merged.Settings.GeoApiKey;
            GcpSaJson = merged.Settings.GcpSaJson;
            AnalyticsSheetId = merged.Settings.AnalyticsSheetId;
            SaveSettings();
            SetupMessage = "Loaded: " + string.Join(", ", applied) + ".";
            _ = RefreshUsageAsync();
        }
        catch (Exception e)
        {
            SetupMessage = e.Message;
        }
    }

    /// <summary>Stores a chosen Drive OAuth client as credentials.json.</summary>
    public async Task LoadDriveCredentialsAsync(IStorageFile file)
    {
        try
        {
            var text = await ReadTextAsync(file);
            JsonNode.Parse(text); // reject a non-JSON file before overwriting
            _platform.SaveDriveCredentials(text);
            RefreshSetupStatus();
            SetupMessage = "Saved credentials.json.";
        }
        catch (Exception e)
        {
            SetupMessage = $"Not a valid credentials.json: {e.Message}";
        }
    }

    /// <summary>
    /// Loads the live usage gauge (best-effort). Hidden when the host has no analytics, no
    /// service account is configured, or Monitoring can't be read — never surfaces an error.
    /// Runs on the UI thread (called from the view / after a geocoded run) so binding updates are safe.
    /// </summary>
    public async Task RefreshUsageAsync()
    {
        if (_usageLoading) return;
        if (!Features.Analytics || string.IsNullOrWhiteSpace(GcpSaJson)) { HasUsage = false; return; }

        _usageLoading = true;
        try
        {
            var gauge = await _platform.GetUsageAsync(GcpSaJson);
            if (gauge is null) { HasUsage = false; return; }

            UsageRingGeometry = Geometry.Parse(UsageRing.ArcGeometry(gauge.Percent));
            UsageColor = UsageRing.GaugeColor(gauge.Percent);
            UsagePercentText = $"{gauge.Percent:0}%";
            var reset = gauge.ResetDays switch
            {
                null => "",
                1 => "\nresets tomorrow",
                var d => $"\nresets in {d} days",
            };
            UsageSubText = $"Places API · this month\n{gauge.Used:N0} / {gauge.Limit:N0}{reset}";
            HasUsage = true;
        }
        finally
        {
            _usageLoading = false;
        }
    }

    /// <summary>Accepts a dropped or picked itinerary, rejecting the wrong type or an oversized file.</summary>
    public async Task LoadInputFileAsync(IStorageFile file)
    {
        var ext = Path.GetExtension(file.Name).ToLowerInvariant();
        if (ext is not (".pdf" or ".txt"))
        {
            ErrorText = "Only PDF or TXT itineraries are supported.";
            return;
        }
        // Check the size before reading, so an accidental huge file is never pulled into memory.
        var size = (await file.GetBasicPropertiesAsync()).Size;
        var maxMb = Features.MaxUploadMb;
        if (size is { } bytes && bytes / 1e6 > maxMb)
        {
            ErrorText = $"File is too large ({bytes / 1e6:F1} MB). Max is {maxMb} MB.";
            return;
        }

        var content = await ReadBytesAsync(file);
        if (content.Length / 1e6 > maxMb)
        {
            ErrorText = $"File is too large ({content.Length / 1e6:F1} MB). Max is {maxMb} MB.";
            return;
        }
        ErrorText = "";
        _inputContent = content;
        InputFileName = file.Name;
    }

    [RelayCommand]
    private void ShowMakeMap() => Page = AppPage.MakeMap;

    [RelayCommand]
    private void ShowAnalytics() => Page = AppPage.Analytics;

    [RelayCommand]
    private void ShowSettings() => Page = AppPage.Settings;

    private bool CanGenerate() => !IsBusy && _inputContent is not null;

    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private async Task GenerateAsync()
    {
        if (string.IsNullOrWhiteSpace(GoogleApiKey))
        {
            // Stay put: the error banner lives on this page, so jumping to Settings
            // would drop the user there with no explanation.
            ErrorText = "No Gemini API key configured — add one on the ⚙️ Settings page.";
            return;
        }
        if (_inputContent is null) return;

        IsBusy = true;
        HasResult = false;
        ErrorText = "";
        Progress = 0;
        ResultFiles.Clear();
        try
        {
            var gemini = new GeminiExtractionService(Http, GoogleApiKey, inlineFiles: Features.InlineFiles);
            var geocoding = new GeocodingService(Http, GeoApiKey);
            var pipeline = new PipelineService(gemini, geocoding);

            var result = await pipeline.GenerateAsync(
                InputFileName,
                _inputContent,
                layersPerFile: LayersPerFile,
                noGeocode: SkipGeocoding,
                progress: (step, frac) =>
                {
                    StatusText = step;
                    Progress = frac;
                });

            ResultTripName = result.TripName;
            ResultDays = result.Days.ToString();
            ResultLocations = result.Locations.ToString();
            ResultExactCoords = $"{result.Corrected}/{result.Corrected + result.Fallback}";
            GeocodeWarning = result.GeocodeWarning ?? "";
            foreach (var kml in result.KmlFiles) ResultFiles.Add(KmlFileItem.From(kml));
            HasResult = true;
            StatusText = "";

            if (PublishEnabled && Features.Publish) await PublishAsync(result.TripName, result.KmlFiles);

            // Log the run to the analytics Google Sheet (best-effort; no-op if unconfigured).
            // Fire-and-forget: logging never throws, and a slow/unreachable Sheet must not keep
            // the finished run "busy". Materialize off the UI collection before firing so the
            // deferred continuation never touches ResultFiles off the UI thread.
            if (Features.Analytics)
            {
                var mapCount = ResultFiles.Count(f => f.HasMap);
                var mapLinks = ResultFiles.Where(f => f.HasMap).Select(f => f.MapUrl).ToList();
                _ = _platform.RecordTripAsync(
                    GcpSaJson, AnalyticsSheetId, result.TripName, mapCount, result.Locations, mapLinks);
            }
        }
        catch (Exception e)
        {
            ErrorText = e.Message;
            StatusText = "";
        }
        finally
        {
            IsBusy = false;
        }

        // A geocoded run just spent Places quota — refresh the gauge once (the only
        // refresh besides app launch), matching the Python app. Skipped when geocoding was off.
        if (!SkipGeocoding) await RefreshUsageAsync();
    }

    /// <summary>
    /// Creates one My Maps map per KML file and shares it. Publishing failing must never
    /// discard the KML files already generated, so this reports into the file rows and the
    /// error banner rather than throwing out of the run.
    /// </summary>
    private async Task PublishAsync(string tripName, IReadOnlyList<KmlFile> files)
    {
        try
        {
            var maps = await _platform.PublishAsync(
                tripName,
                files,
                ShareEmailsList.ToList(),
                SelectedRole,
                NotifyShare,
                ShowBrowser,
                (step, frac) =>
                {
                    StatusText = step;
                    Progress = frac;
                });

            var byFile = ResultFiles.ToDictionary(f => f.FileName, StringComparer.OrdinalIgnoreCase);
            foreach (var map in maps)
            {
                if (!byFile.TryGetValue(map.FileName, out var row)) continue;
                row.MapError = map.Error;
                if (map.Error.Length == 0)
                {
                    row.MapUrl = map.ViewUrl;
                    row.SharedWith = map.SharedWith.Count > 0
                        ? $"Shared with: {string.Join(", ", map.SharedWith)}"
                        : "Not shared";
                }
            }

            var ok = maps.Count(m => m.Error.Length == 0);
            StatusText = "";
            if (ok < maps.Count)
                ErrorText = $"Published {ok}/{maps.Count} map(s) — see the per-file notes below.";
        }
        catch (Exception e)
        {
            // Auth/setup failure before the per-file loop: the KML files still exist.
            StatusText = "";
            ErrorText = $"Maps couldn't be published (the KML files were still created): {e.Message}";
        }
    }

    [RelayCommand]
    private async Task LogInToGoogleAsync()
    {
        IsLoggingIn = true;
        LoginStatus = "Opening a browser window — sign in to Google, then return here…";
        try
        {
            await _platform.LoginAsync();
            LoginStatus = "✅ Signed in to Google. The session is saved for future runs.";
        }
        catch (Exception e)
        {
            LoginStatus = $"⚠️ Login failed: {e.Message}";
        }
        finally
        {
            IsLoggingIn = false;
        }
    }

    [RelayCommand]
    private async Task CheckLoginAsync()
    {
        IsLoggingIn = true;
        LoginStatus = "Checking the saved Google session…";
        try
        {
            LoginStatus = await _platform.IsLoggedInAsync()
                ? "✅ Signed in to Google."
                : "⚠️ Not signed in — click 'Log in to Google'.";
        }
        catch (Exception e)
        {
            LoginStatus = $"⚠️ Could not check the session: {e.Message}";
        }
        finally
        {
            IsLoggingIn = false;
        }
    }

    [RelayCommand]
    private void OpenMap(KmlFileItem? item)
    {
        if (item is { HasMap: true }) _platform.OpenUrl(item.MapUrl);
    }

    // --- Update commands ----------------------------------------------------
    private bool NotChecking() => !IsCheckingUpdate;

    [RelayCommand(CanExecute = nameof(NotChecking))]
    private async Task CheckForUpdatesAsync()
    {
        IsCheckingUpdate = true;
        UpdateAvailable = false;
        UpdateStatus = "Checking for updates…";
        _pendingUpdate = null;
        try
        {
            var info = await _platform.CheckForUpdateAsync();
            if (info is null) { UpdateStatus = "Couldn't check for updates — check your connection."; return; }
            if (!info.HasUpdate) { UpdateStatus = $"You're on the latest version (v{info.Current})."; return; }

            _pendingUpdate = info;
            if (info.HasAsset && UpdateService.IsSelfUpdateSupported)
            {
                UpdateAvailable = true;
                UpdateStatus = $"Version {info.Latest} is available.";
            }
            else
            {
                // Reachable release but no installer for this OS — point at the page instead.
                UpdateStatus = $"Version {info.Latest} is available — download it from the releases page.";
            }
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    private bool CanInstallUpdate() => !IsCheckingUpdate && _pendingUpdate is { HasAsset: true };

    [RelayCommand(CanExecute = nameof(CanInstallUpdate))]
    private async Task DownloadAndInstallUpdateAsync()
    {
        if (_pendingUpdate is not { HasAsset: true } info) return;
        IsCheckingUpdate = true;
        try
        {
            UpdateStatus = "Downloading update…";
            await _platform.InstallUpdateAsync(info, p => UpdateStatus = $"Downloading update… {p:P0}");
            UpdateStatus = "Starting the installer…";
        }
        catch (Exception e)
        {
            UpdateStatus = $"Update failed: {e.Message}";
        }
        finally
        {
            IsCheckingUpdate = false;
        }
    }

    [RelayCommand]
    private void OpenReleasePage()
    {
        var url = _pendingUpdate is { HtmlUrl.Length: > 0 } info
            ? info.HtmlUrl
            : $"https://github.com/{AppConfig.GithubRepo}/releases";
        _platform.OpenUrl(url);
    }

    /// <summary>Hands the generated KML to the user (desktop: a chosen folder; browser: downloads).</summary>
    public async Task SaveKmlFilesAsync(IStorageProvider storage)
    {
        if (ResultFiles.Count == 0) return;
        try
        {
            var status = await _platform.SaveKmlFilesAsync(storage, ResultFiles.Select(f => f.File).ToList());
            if (status.Length > 0) StatusText = status;
        }
        catch (Exception e)
        {
            ErrorText = $"Couldn't save the KML files: {e.Message}";
        }
    }

    private static async Task<byte[]> ReadBytesAsync(IStorageFile file)
    {
        await using var stream = await file.OpenReadAsync();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static async Task<string> ReadTextAsync(IStorageFile file)
    {
        await using var stream = await file.OpenReadAsync();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}
```

The update flow has one intentional ordering change. Before, the view model set "Starting the installer…" and then called `ApplyUpdate`, which exits the app on Windows. Now `InstallUpdateAsync` does both steps, so that status line only shows if the process didn't exit (macOS, where it opens the .dmg). Everything else in this file matches the previous version.

- [ ] **Step 5: Rewrite the view code-behind to pass storage files, not paths**

Replace the entire contents of `src/GmapPlanner.UI/Views/MainView.axaml.cs` with:

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
        DownloadButton.Click += async (_, _) => await SafeAsync(() => Vm?.SaveKmlFilesAsync(Storage) ?? Task.CompletedTask);
        SetupBundleButton.Click += async (_, _) => await SafeAsync(BrowseSetupBundleAsync);
        CredentialsButton.Click += async (_, _) => await SafeAsync(BrowseCredentialsAsync);

        // Email token input: Enter/Tab/separators commit a chip; Backspace on empty pops one;
        // losing focus commits whatever's half-typed so it isn't silently lost.
        EmailEntry.KeyDown += OnEmailEntryKeyDown;
        EmailEntry.LostFocus += (_, _) => CommitEmailEntry();

        // Drop a file onto the itinerary zone, or straight onto the Settings pickers.
        EnableFileDrop(DropZone, (vm, f) => vm.LoadInputFileAsync(f));
        EnableFileDrop(SetupBundleButton, (vm, f) => vm.LoadSetupBundleAsync(f));
        EnableFileDrop(CredentialsButton, (vm, f) => vm.LoadDriveCredentialsAsync(f));

        // Hold the eye to reveal a masked API key; release (or leave) re-masks it.
        WireHoldReveal(GeminiKeyEye, GeminiKeyBox);
        WireHoldReveal(GeoKeyEye, GeoKeyBox);

        // Load the usage gauge once the view is up, on the UI thread so binding is safe.
        Loaded += async (_, _) =>
        {
            if (Vm is { } vm) await vm.RefreshUsageAsync();
        };
    }

    private MainViewModel? Vm => DataContext as MainViewModel;

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
        var file = (await Storage.OpenFilePickerAsync(JsonPicker("Choose a setup file"))).FirstOrDefault();
        if (file is not null && Vm is { } vm) await vm.LoadSetupBundleAsync(file);
    }

    private async Task BrowseCredentialsAsync()
    {
        var file = (await Storage.OpenFilePickerAsync(JsonPicker("Choose the Drive credentials.json"))).FirstOrDefault();
        if (file is not null && Vm is { } vm) await vm.LoadDriveCredentialsAsync(file);
    }

    private async Task BrowseInputFileAsync()
    {
        var file = (await Storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Choose an itinerary",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Itinerary (*.pdf, *.txt)") { Patterns = ["*.pdf", "*.txt"] },
            ],
        })).FirstOrDefault();
        if (file is not null && Vm is { } vm) await vm.LoadInputFileAsync(file);
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
        catch (Exception e) when (Vm is { } vm)
        {
            vm.ErrorText = e.Message;
        }
    }

    /// <summary>Lets a control accept a dropped file, handing the first file to the VM.</summary>
    private void EnableFileDrop(Control target, Func<MainViewModel, IStorageFile, Task> onFile)
    {
        DragDrop.SetAllowDrop(target, true);
        target.AddHandler(DragDrop.DragOverEvent, (_, e) =>
            e.DragEffects = e.Data.Contains(DataFormats.Files) ? DragDropEffects.Copy : DragDropEffects.None);
        target.AddHandler(DragDrop.DropEvent, async (_, e) => await SafeAsync(async () =>
        {
            if (e.Data.GetFiles()?.OfType<IStorageFile>().FirstOrDefault() is { } file && Vm is { } vm)
                await onFile(vm, file);
        }));
    }

    /// <summary>Reveals a password TextBox while the eye is held (tunnel, so the Button can't swallow it).</summary>
    private static void WireHoldReveal(Control eye, TextBox box)
    {
        eye.AddHandler(InputElement.PointerPressedEvent, (_, _) => box.RevealPassword = true, RoutingStrategies.Tunnel);
        eye.AddHandler(InputElement.PointerReleasedEvent, (_, _) => box.RevealPassword = false, RoutingStrategies.Tunnel);
        // Releasing off the button (drag away) still fires PointerExited — re-mask there too.
        eye.PointerExited += (_, _) => box.RevealPassword = false;
    }

    private void OnEmailEntryKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb) return;

        // Backspace with nothing typed removes the last chip.
        if (e.Key == Key.Back && string.IsNullOrEmpty(tb.Text))
        {
            Vm?.RemoveLastEmail();
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
        if (Vm is not { } vm || string.IsNullOrWhiteSpace(EmailEntry.Text)) return false;
        vm.AddEmails(EmailEntry.Text);
        EmailEntry.Text = "";
        return true;
    }
}
```

- [ ] **Step 6: Bind the view's host-specific parts to `Features`**

All edits are in `src/GmapPlanner.UI/Views/MainView.axaml`. Each is an exact text replacement; read the file first so the edits match.

6a. Delete the design-time data context (it needs a parameterless constructor, which no longer exists):

```xml
    <Design.DataContext>
        <vm:MainViewModel />
    </Design.DataContext>
```

6b. Hide the Analytics nav button when the host has no analytics. Replace:

```xml
                    <Button Classes="nav" Command="{Binding ShowAnalyticsCommand}">
```

with:

```xml
                    <Button Classes="nav" Command="{Binding ShowAnalyticsCommand}" IsVisible="{Binding Features.Analytics}">
```

6c. Wrap the whole "PUBLISH TO MY MAPS" sidebar block. Replace the opening lines:

```xml
                    <Separator Margin="0,14" Background="{StaticResource HairlineBrush}" />

                    <TextBlock Classes="sideHeader" Text="PUBLISH TO MY MAPS" />
```

with:

```xml
                    <StackPanel Spacing="6" IsVisible="{Binding Features.Publish}">
                    <Separator Margin="0,14" Background="{StaticResource HairlineBrush}" />

                    <TextBlock Classes="sideHeader" Text="PUBLISH TO MY MAPS" />
```

and close it right after the publish options panel. Replace:

```xml
                        <TextBlock Classes="caption"
                                   Text="Headless by default. Turn on to watch or debug the automation." />
                    </StackPanel>
                </StackPanel>
```

with:

```xml
                        <TextBlock Classes="caption"
                                   Text="Headless by default. Turn on to watch or debug the automation." />
                    </StackPanel>
                    </StackPanel>
                </StackPanel>
```

6d. Settings cards. Replace `<!-- Service-account JSON (usage gauge) -->` followed by `<Border Classes="card">` (the next line) with:

```xml
                    <!-- Service-account JSON (usage gauge) -->
                    <Border Classes="card" IsVisible="{Binding Features.Analytics}">
```

Replace `<!-- Analytics Sheet -->` followed by `<Border Classes="card">` with:

```xml
                    <!-- Analytics Sheet -->
                    <Border Classes="card" IsVisible="{Binding Features.Analytics}">
```

Replace `<!-- Drive sharing credentials -->` followed by `<Border Classes="card">` with:

```xml
                    <!-- Drive sharing credentials -->
                    <Border Classes="card" IsVisible="{Binding Features.Publish}">
```

Replace `<!-- About & updates -->` followed by `<Border Classes="card">` with:

```xml
                    <!-- About & updates -->
                    <Border Classes="card" IsVisible="{Binding Features.Updates}">
```

6e. Setup-status rows for login/Drive. Replace each of the three opening `<TextBlock>` tags that directly precede the runs bound to `HasGoogleLogin`, `HasDriveCredentials` and `HasDriveToken` with `<TextBlock IsVisible="{Binding Features.Publish}">`. Leave the Gemini and Places key rows unchanged.

Check: `grep -c "Features\." src/GmapPlanner.UI/Views/MainView.axaml` → `9` (1 nav + 1 publish block + 4 cards + 3 status rows).

- [ ] **Step 7: Rewire the projects and the desktop host**

In `src/GmapPlanner.UI/GmapPlanner.UI.csproj`:
- Delete the line `<ProjectReference Include="..\GmapPlanner.Core.Publish\GmapPlanner.Core.Publish.csproj" />`.
- Delete the `<PlaywrightPlatform>none</PlaywrightPlatform>` element and the XML comment directly above it. UI no longer references Playwright, even transitively.
- Update the project's top comment so it says: shared by the desktop exe and the browser host; references only `GmapPlanner.Core`; host-specific code goes through `Platform/IPlatformServices`.

In `src/GmapPlanner.App/GmapPlanner.App.csproj`, replace:

```xml
    <ProjectReference Include="..\GmapPlanner.UI\GmapPlanner.UI.csproj" />
```

with:

```xml
    <ProjectReference Include="..\GmapPlanner.UI\GmapPlanner.UI.csproj" />
    <ProjectReference Include="..\GmapPlanner.Core.Publish\GmapPlanner.Core.Publish.csproj" />
```

In `src/GmapPlanner.App/App.axaml.cs`, add `using GmapPlanner.App.Platform;` to the usings, and replace:

```csharp
                DataContext = new MainViewModel(),
```

with:

```csharp
                DataContext = new MainViewModel(new DesktopPlatformServices()),
```

- [ ] **Step 8: Build, verify the reference graph, run tests**

Close any running `GmapPlanner.App`, then run: `dotnet build`
Expected: `Build succeeded.` `0 Error(s)`.

Run: `dotnet list src/GmapPlanner.UI/GmapPlanner.UI.csproj reference`
Expected: exactly one reference, `..\GmapPlanner.Core\GmapPlanner.Core.csproj`.

Run: `grep -rn "Core.Services.Publish\|Microsoft.Playwright\|Google.Apis" src/GmapPlanner.UI --include=*.cs`
Expected: no output.

Run: `dotnet test tests/GmapPlanner.Core.Tests`
Expected: 104 passed, 0 failed.

- [ ] **Step 9: Verify the Playwright driver still lands per-RID**

```bash
rm -rf src/GmapPlanner.UI/bin src/GmapPlanner.UI/obj src/GmapPlanner.App/bin src/GmapPlanner.App/obj
dotnet build src/GmapPlanner.App -r osx-arm64
ls src/GmapPlanner.App/bin/Debug/net8.0/osx-arm64/.playwright/node/
```

Expected: exactly `LICENSE` and `darwin-arm64`.

- [ ] **Step 10: Desktop smoke test**

Run: `dotnet run --project src/GmapPlanner.App`. It must launch and stay up (no crash, no fresh `%APPDATA%/GmapPlanner/last_error.log`). A human checks the items below. An implementer subagent that can't click the GUI reports the launch check it ran and lists these under "Needs human verification":
1. Settings shows all cards (usage gauge, Analytics Sheet, Drive credentials, setup status with all 5 rows, About & updates), and the sidebar shows the Analytics nav button and the PUBLISH TO MY MAPS block.
2. Keys typed in Settings survive an app restart.
3. Dropping or picking a `.txt` itinerary shows its file name; Generate produces the result cards and per-file cards.
4. "Download KML files" opens a folder picker, writes the files, and reveals the folder.
5. With "Create & share maps" on, publishing still creates maps (if a Google session exists).

- [ ] **Step 11: Commit**

```bash
git add -A src/GmapPlanner.UI src/GmapPlanner.App
git status --short
git commit -m "UI: route host-specific work through IPlatformServices; desktop implementation

GmapPlanner.UI now references only Core, so the browser host can reuse it.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 4: `GmapPlanner.App.Browser` — the WebAssembly host

**Prerequisites (controller confirms with the user before dispatch):** `wasm-tools-net8` workload installed from an Administrator terminal; explicit approval to download the Noto Sans Hebrew font (see Prerequisites at the top).

**Files:**
- Create: `src/GmapPlanner.App.Browser/GmapPlanner.App.Browser.csproj`
- Create: `src/GmapPlanner.App.Browser/Program.cs`
- Create: `src/GmapPlanner.App.Browser/App.axaml`, `src/GmapPlanner.App.Browser/App.axaml.cs`
- Create: `src/GmapPlanner.App.Browser/Platform/BrowserPlatformServices.cs`
- Create: `src/GmapPlanner.App.Browser/wwwroot/index.html`, `wwwroot/main.js`, `wwwroot/app.css`
- Create: `src/GmapPlanner.App.Browser/Properties/launchSettings.json`
- Create (download): `src/GmapPlanner.App.Browser/Assets/Fonts/NotoSansHebrew.ttf`
- Modify: `GmapPlanner.sln`

**Interfaces:**
- Consumes (Task 3): `IPlatformServices` and its members, `PlatformFeatures`, `SetupStatus`, `MapResult`, `new MainViewModel(IPlatformServices)`, `GmapPlanner.App.Views.MainView`, `avares://GmapPlanner.UI/Theme.axaml`.
- Consumes (Task 2): `AppSettingsService.ToJson/FromJson`.
- Produces: a static site in `dotnet publish src/GmapPlanner.App.Browser -c Release -o publish/browser` → `publish/browser/wwwroot/` (with `index.html`, `main.js`, `app.css`, `_framework/`). Task 5 deploys exactly that folder.
- Produces: `BrowserPlatformServices.Features = new(Publish: false, Analytics: false, Updates: false, InlineFiles: true, MaxUploadMb: 14)`.

- [ ] **Step 1: Create the project and register it**

Create `src/GmapPlanner.App.Browser/GmapPlanner.App.Browser.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.WebAssembly">

  <!-- Browser host: the shared UI compiled to WebAssembly, served as static files (Vercel).
       Build needs the wasm-tools-net8 workload (SkiaSharp/HarfBuzz native relink).
       References GmapPlanner.UI only — never Core.Publish (no Playwright/Google.Apis in a browser). -->
  <PropertyGroup>
    <TargetFramework>net8.0-browser</TargetFramework>
    <OutputType>Exe</OutputType>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <!-- Same size choice as the desktop app; no culture data needed. -->
    <InvariantGlobalization>true</InvariantGlobalization>
    <!-- Publish trims. SharpKml serializes by reflection over its own DOM (CLAUDE.md
         trimming rule 3), so keep it whole; partial mode leaves untrimmable libraries alone. -->
    <TrimMode>partial</TrimMode>
  </PropertyGroup>

  <ItemGroup>
    <TrimmerRootAssembly Include="SharpKml.Core" />
  </ItemGroup>

  <ItemGroup>
    <AvaloniaResource Include="Assets\**" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="Avalonia" Version="11.2.5" />
    <PackageReference Include="Avalonia.Browser" Version="11.2.5" />
    <PackageReference Include="Avalonia.Themes.Fluent" Version="11.2.5" />
    <PackageReference Include="Avalonia.Fonts.Inter" Version="11.2.5" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\GmapPlanner.UI\GmapPlanner.UI.csproj" />
  </ItemGroup>

</Project>
```

Run: `dotnet sln GmapPlanner.sln add src/GmapPlanner.App.Browser/GmapPlanner.App.Browser.csproj --solution-folder src`
Expected: `Project ... added to the solution.`

- [ ] **Step 2: Add the Hebrew fallback font (download — approved by the user before dispatch)**

```bash
mkdir -p src/GmapPlanner.App.Browser/Assets/Fonts
curl -fL -o src/GmapPlanner.App.Browser/Assets/Fonts/NotoSansHebrew.ttf "https://github.com/google/fonts/raw/main/ofl/notosanshebrew/NotoSansHebrew%5Bwdth%2Cwght%5D.ttf"
ls -l src/GmapPlanner.App.Browser/Assets/Fonts/NotoSansHebrew.ttf
```

Expected: a file of roughly 50–200 KB. Check it's a TrueType file: `head -c 4 src/GmapPlanner.App.Browser/Assets/Fonts/NotoSansHebrew.ttf | od -An -tx1` prints `00 01 00 00`. If the download fails or the header differs, stop and report BLOCKED. Don't substitute a different font source.

Create `src/GmapPlanner.App.Browser/Assets/Fonts/OFL.txt` with this single line (the font's license is the SIL Open Font License 1.1; the full text lives at the URL):

```
Noto Sans Hebrew — Copyright 2022 The Noto Project Authors (https://github.com/notofonts/hebrew). Licensed under the SIL Open Font License, Version 1.1: https://openfontlicense.org
```

- [ ] **Step 3: Program and App**

Create `src/GmapPlanner.App.Browser/Program.cs`:

```csharp
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Browser;
using Avalonia.Media;
using GmapPlanner.App.Browser;

[assembly: SupportedOSPlatform("browser")]

internal sealed partial class Program
{
    private static Task Main(string[] args) => BuildAvaloniaApp()
        .WithInterFont()
        .With(new FontManagerOptions
        {
            // A browser has no system fonts: Inter is the default face, and Inter has no Hebrew,
            // so Hebrew trip names/notes fall back to the bundled Noto Sans Hebrew.
            DefaultFamilyName = "fonts:Inter#Inter",
            FontFallbacks =
            [
                new FontFallback { FontFamily = new FontFamily("avares://GmapPlanner.App.Browser/Assets/Fonts#Noto Sans Hebrew") },
            ],
        })
        .StartBrowserAppAsync("out");

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>();
}
```

Create `src/GmapPlanner.App.Browser/App.axaml`:

```xml
<Application xmlns="https://github.com/avaloniaui"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             x:Class="GmapPlanner.App.Browser.App"
             RequestedThemeVariant="Dark">
    <!-- Same design system as the desktop host (GmapPlanner.UI/Theme.axaml). -->
    <Application.Styles>
        <FluentTheme />
        <StyleInclude Source="avares://GmapPlanner.UI/Theme.axaml" />
    </Application.Styles>
</Application>
```

Create `src/GmapPlanner.App.Browser/App.axaml.cs`:

```csharp
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using GmapPlanner.App.Platform;
using GmapPlanner.App.ViewModels;
using GmapPlanner.App.Views;

namespace GmapPlanner.App.Browser;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is ISingleViewApplicationLifetime singleView)
        {
            singleView.MainView = new MainView
            {
                DataContext = new MainViewModel(new BrowserPlatformServices()),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
```

- [ ] **Step 4: The browser platform implementation**

Create `src/GmapPlanner.App.Browser/Platform/BrowserPlatformServices.cs`:

```csharp
using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Avalonia.Platform.Storage;
using GmapPlanner.Core.Services;

namespace GmapPlanner.App.Platform;

/// <summary>
/// The browser host. Settings live in this browser's localStorage; KML files are handed over
/// as downloads. Publishing, Google login, usage/analytics and updates are off (the view hides
/// them) until phases 3–4 route them through the Vercel API.
/// </summary>
[SupportedOSPlatform("browser")]
public sealed partial class BrowserPlatformServices : IPlatformServices
{
    private const string SettingsKey = "gmapplanner.settings";
    private const string KmlMimeType = "application/vnd.google-earth.kml+xml";

    public PlatformFeatures Features { get; } = new(Publish: false, Analytics: false, Updates: false, InlineFiles: true, MaxUploadMb: 14);

    public AppSettings LoadSettings() => AppSettingsService.FromJson(GetItem(SettingsKey));

    public void SaveSettings(AppSettings settings) => SetItem(SettingsKey, AppSettingsService.ToJson(settings));

    public SetupStatus GetSetupStatus() => new(HasGoogleLogin: false, HasDriveCredentials: false, HasDriveToken: false);

    public void SaveDriveCredentials(string json) =>
        throw new NotSupportedException("Drive credentials aren't used by the web version yet.");

    public void OpenUrl(string url) => OpenUrlJs(url);

    public Task<string> SaveKmlFilesAsync(IStorageProvider storage, IReadOnlyList<KmlFile> files)
    {
        foreach (var file in files) DownloadText(file.FileName, file.Content, KmlMimeType);
        return Task.FromResult($"Downloaded {files.Count} file(s).");
    }

    public Task<UsageGauge?> GetUsageAsync(string serviceAccountJson) => Task.FromResult<UsageGauge?>(null);

    public Task<IReadOnlyList<AnalyticsRow>?> FetchAnalyticsAsync(string serviceAccountJson, string sheetId) =>
        Task.FromResult<IReadOnlyList<AnalyticsRow>?>(null);

    public Task RecordTripAsync(string serviceAccountJson, string sheetId, string tripName, int maps, int places, IReadOnlyList<string> mapLinks) =>
        Task.CompletedTask;

    public Task<IReadOnlyList<MapResult>> PublishAsync(
        string tripName, IReadOnlyList<KmlFile> files, IReadOnlyList<string> recipients,
        string role, bool notify, bool showBrowser, ProgressCallback progress) =>
        throw new NotSupportedException("Publishing to My Maps isn't available in the web version yet.");

    public Task LoginAsync() =>
        throw new NotSupportedException("Google login isn't available in the web version yet.");

    public Task<bool> IsLoggedInAsync() => Task.FromResult(false);

    public Task<UpdateInfo?> CheckForUpdateAsync() => Task.FromResult<UpdateInfo?>(null);

    public Task InstallUpdateAsync(UpdateInfo update, Action<double> progress) =>
        throw new NotSupportedException("The web version updates itself on reload.");

    // Helpers defined on globalThis.gmapPlanner in wwwroot/main.js.
    [JSImport("globalThis.gmapPlanner.getItem")]
    private static partial string? GetItem(string key);

    [JSImport("globalThis.gmapPlanner.setItem")]
    private static partial void SetItem(string key, string value);

    [JSImport("globalThis.gmapPlanner.openUrl")]
    private static partial void OpenUrlJs(string url);

    [JSImport("globalThis.gmapPlanner.downloadText")]
    private static partial void DownloadText(string fileName, string content, string mimeType);
}
```

- [ ] **Step 5: Static web files**

Create `src/GmapPlanner.App.Browser/wwwroot/index.html`:

```html
<!DOCTYPE html>
<html lang="en">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>My Maps Generator</title>
    <link rel="modulepreload" href="./main.js" />
    <link rel="modulepreload" href="./_framework/dotnet.js" />
    <link rel="stylesheet" href="./app.css" />
</head>
<body>
    <div id="out">
        <div class="avalonia-splash">
            <h2>Loading My Maps Generator…</h2>
        </div>
    </div>
    <script type="module" src="./main.js"></script>
</body>
</html>
```

Create `src/GmapPlanner.App.Browser/wwwroot/main.js`:

```js
import { dotnet } from './_framework/dotnet.js';

// Browser helpers the .NET side calls through [JSImport] (BrowserPlatformServices).
// Defined before the runtime starts so they exist when the app first loads its settings.
globalThis.gmapPlanner = {
    getItem: (key) => {
        try { return localStorage.getItem(key); } catch { return null; }
    },
    setItem: (key, value) => {
        try { localStorage.setItem(key, value); } catch { /* private mode or quota: settings just don't persist */ }
    },
    openUrl: (url) => {
        window.open(url, '_blank', 'noopener');
    },
    downloadText: (fileName, content, mimeType) => {
        const url = URL.createObjectURL(new Blob([content], { type: mimeType }));
        const link = document.createElement('a');
        link.href = url;
        link.download = fileName;
        document.body.appendChild(link);
        link.click();
        link.remove();
        setTimeout(() => URL.revokeObjectURL(url), 10000);
    },
};

const dotnetRuntime = await dotnet
    .withDiagnosticTracing(false)
    .withApplicationArgumentsFromQuery()
    .create();

const config = dotnetRuntime.getConfig();
await dotnetRuntime.runMain(config.mainAssemblyName, [globalThis.location.href]);
```

Create `src/GmapPlanner.App.Browser/wwwroot/app.css`:

```css
html, body {
    margin: 0;
    height: 100%;
    overflow: hidden;
    background: #0B1120;
}

#out {
    width: 100vw;
    height: 100vh;
}

.avalonia-splash {
    position: absolute;
    inset: 0;
    display: flex;
    align-items: center;
    justify-content: center;
    background: #0B1120;
    color: #E6EDF7;
    font-family: system-ui, sans-serif;
}

.avalonia-splash.splash-close {
    transition: opacity 200ms;
    opacity: 0;
}
```

Create `src/GmapPlanner.App.Browser/Properties/launchSettings.json`:

```json
{
  "profiles": {
    "GmapPlanner.App.Browser": {
      "commandName": "Project",
      "launchBrowser": true,
      "applicationUrl": "http://localhost:5235",
      "inspectUri": "{wsProtocol}://{url.hostname}:{url.port}/_framework/debug/ws-proxy?browser={browserInspectUri}"
    }
  }
}
```

- [ ] **Step 6: Build, publish, and verify the bundle**

Run: `dotnet workload list` — Expected: `wasm-tools-net8` listed. If it's missing, stop and report BLOCKED ("wasm-tools-net8 workload not installed"). Don't try to install it: that needs an Administrator terminal.

Run: `dotnet build`
Expected: `Build succeeded.` `0 Error(s)` for the whole solution, including the browser project.

Run: `dotnet list src/GmapPlanner.App.Browser/GmapPlanner.App.Browser.csproj reference`
Expected: exactly `..\GmapPlanner.UI\GmapPlanner.UI.csproj`.

Run: `dotnet publish src/GmapPlanner.App.Browser -c Release -o publish/browser 2>&1 | tee publish-browser.log`
Then:

```bash
ls publish/browser/wwwroot/index.html publish/browser/wwwroot/main.js publish/browser/wwwroot/_framework/dotnet.js
ls publish/browser/wwwroot/_framework | grep -i -E "playwright|google\.apis|newtonsoft" ; echo "forbidden-assemblies-check-done"
du -sh publish/browser/wwwroot
grep "IL2026" publish-browser.log | grep -v -i "avalonia" | head -20
```

Expected:
- The three files exist.
- The `grep` for Playwright/Google.Apis/Newtonsoft prints nothing before `forbidden-assemblies-check-done`.
- Record the bundle size in the report; anything under 60 MB uncompressed is expected.
- Record any IL2026 lines in the report. None may come from `GmapPlanner.*` assemblies; if any do, stop and report NEEDS_CONTEXT with the lines.

Delete the output afterwards: `rm -rf publish/browser publish-browser.log`.

- [ ] **Step 7: Run the site locally**

Run: `dotnet run --project src/GmapPlanner.App.Browser` and open the printed `http://localhost:…` URL in Chrome.

An implementer subagent that can't drive a browser should instead check that the dev server starts and that `curl -s http://localhost:5235/ | grep -c "My Maps Generator"` prints `1`. It then stops the server and lists the items below under "Needs human verification". A human checks:
1. The page loads past the splash into the dark UI. The sidebar shows Make Map and Settings, with **no** Analytics button and **no** PUBLISH TO MY MAPS block.
2. The Settings page shows One-file setup, Gemini key, Places key, and Setup status (Gemini/Places rows only). There's no usage gauge, Analytics Sheet, Drive credentials, or About & updates card.
3. Type both keys, reload the page, and the keys are still there.
4. Pick a small `.txt` itinerary containing Hebrew place names, then Generate. Result cards appear, the Hebrew renders as Hebrew (not boxes), and "Exact coords" is above 0/N. That last check proves the browser's Places API (New) call works cross-origin. If it's 0/N with a CORS or network error, note the exact banner text.
5. Pick a small `.pdf` itinerary, then Generate. It succeeds through inline data.
6. "Download KML files" downloads each `.kml`. Chrome may ask once to allow multiple downloads.
7. A 20 MB file is rejected with "Max is 14 MB."

- [ ] **Step 8: Commit**

```bash
git add GmapPlanner.sln src/GmapPlanner.App.Browser
git status --short
git commit -m "Add GmapPlanner.App.Browser: Avalonia WebAssembly host with browser platform services

Settings in localStorage, KML as downloads, PDFs inline to Gemini, bundled Hebrew
fallback font. Publishing/analytics/updates hidden until phases 3-4.

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

---

### Task 5: Deploy workflow, documentation, and the first live deploy

**Files:**
- Create: `.github/workflows/web.yml`
- Modify: `CLAUDE.md`
- Modify: `docs/superpowers/specs/2026-09-15-cloud-hosting-design.md` (`## Deployment` section; `## Build order` item 2)

**Interfaces:**
- Consumes (Task 4): `dotnet publish src/GmapPlanner.App.Browser -c Release -o publish/browser` produces the static site in `publish/browser/wwwroot/`.
- Consumes (human): GitHub repo secrets `VERCEL_TOKEN`, `VERCEL_ORG_ID`, `VERCEL_PROJECT_ID`.
- Produces: pushes to `feature/cloud-hosting` deploy a Vercel **preview**; pushes to `main` deploy **production**. `workflow_dispatch` runs it by hand.

- [ ] **Step 1: Create the workflow**

Create `.github/workflows/web.yml`:

```yaml
name: Web

# Builds the Avalonia WebAssembly site and uploads the static output to Vercel.
# Built here, not on Vercel: the bundle needs the wasm-tools workload (emscripten), which
# Vercel's build image doesn't have. main deploys to production; other branches get a preview.
on:
  push:
    branches: [main, feature/cloud-hosting]
    paths: ['src/**', 'tests/**', '.github/workflows/web.yml']
  workflow_dispatch:

concurrency:
  group: web-${{ github.ref }}
  cancel-in-progress: true

jobs:
  deploy:
    runs-on: ubuntu-latest
    # Forks don't have the Vercel secrets; only this repository deploys.
    if: github.repository == 'BenDayan123/map-generator'
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: '8.0.x'

      - name: Install WebAssembly build tools
        run: dotnet workload install wasm-tools

      - name: Test
        run: dotnet test tests/GmapPlanner.Core.Tests

      - name: Publish browser app
        run: dotnet publish src/GmapPlanner.App.Browser -c Release -o publish/browser

      - name: Deploy to Vercel
        env:
          VERCEL_TOKEN: ${{ secrets.VERCEL_TOKEN }}
          VERCEL_ORG_ID: ${{ secrets.VERCEL_ORG_ID }}
          VERCEL_PROJECT_ID: ${{ secrets.VERCEL_PROJECT_ID }}
        run: |
          PROD_FLAG=""
          if [ "$GITHUB_REF" = "refs/heads/main" ]; then PROD_FLAG="--prod"; fi
          URL=$(npx --yes vercel@latest deploy publish/browser/wwwroot $PROD_FLAG --yes --token "$VERCEL_TOKEN")
          echo "Deployed: $URL"
          echo "### Deployed to $URL" >> "$GITHUB_STEP_SUMMARY"
```

Check the YAML parses: `python -c "import yaml,sys; yaml.safe_load(open('.github/workflows/web.yml'))" && echo yaml-ok`. If Python/PyYAML isn't available, record that in the report and review the indentation by eye instead.

- [ ] **Step 2: Document the browser host in CLAUDE.md**

In `CLAUDE.md`, `## Solution structure` code block:
- Under `GmapPlanner.UI/`, add a line after `Theme.axaml …`: `    Platform/IPlatformServices.cs # host seam: desktop + browser implementations`
- After the line `  GmapPlanner.App/            # desktop exe host: Program, App.axaml, thin MainWindow, Assets`, add:

```
    Platform/DesktopPlatformServices.cs # files, Playwright, Drive, Sheets, Monitoring, updater
  GmapPlanner.App.Browser/    # WebAssembly host (Vercel): BrowserPlatformServices, wwwroot, Hebrew font
```

In `## Build / run / test`, after the three-command code block, add:

```markdown
The browser host needs the `wasm-tools-net8` workload (install once from an Administrator
terminal: `dotnet workload install wasm-tools-net8`); without it, a solution-wide
`dotnet build` fails on `GmapPlanner.App.Browser` — build `src/GmapPlanner.App` directly
instead. Run the site locally with `dotnet run --project src/GmapPlanner.App.Browser`.
```

Replace the entire `### Playwright's node driver is per-platform` trap 1 paragraph (the numbered item starting `1. **Library leaks the host driver.**`) with:

```markdown
1. **Library leaks the host driver.** Any project that references Microsoft.Playwright, directly
   or *transitively*, builds RID-agnostic, so `Microsoft.Playwright.targets` resolves the driver
   off the *build host* (win32_x64 on Windows) and it rides into the mac publish. Every such
   library sets `<PlaywrightPlatform>none</PlaywrightPlatform>` — a library bundles no driver;
   the app does. Today that's only `GmapPlanner.Core.Publish`: `GmapPlanner.UI` reaches
   Playwright no longer (host-specific code goes through `IPlatformServices`), and
   `GmapPlanner.App.Browser` must never reference Core.Publish. The trap reappeared once, when
   `GmapPlanner.UI` briefly referenced Core.Publish without the property.
```

In the `## UI` section, add this paragraph at the end:

```markdown
`MainViewModel` never calls host-specific code directly: settings storage, file
save/download, publishing, Google login, usage/analytics and the updater all go through
`Platform/IPlatformServices` (`DesktopPlatformServices` in the exe, `BrowserPlatformServices`
in the WASM host). `PlatformFeatures` says what a host supports and the view binds
`IsVisible` to it, so the browser simply doesn't show publish/analytics/update controls until
later phases enable them. Input arrives as `IStorageFile` → bytes (the browser has no paths),
and generation is `PipelineService.GenerateAsync` in memory.
```

Add a new section before `## Not ported (yet)`:

```markdown
## Browser host (cloud hosting, phase 2)

- **`GmapPlanner.App.Browser`** — the shared UI compiled to WebAssembly (`net8.0-browser`,
  Avalonia.Browser), served as static files from Vercel. Settings live in the browser's
  localStorage (per device), KML files are downloads, and Gemini gets PDFs as base64
  `inline_data` (`GeminiExtractionService(inlineFiles: true)`) because the Files API upload
  reads a response header a cross-origin fetch can't see; the browser's upload cap is 14 MB
  to stay under Gemini's 20 MB inline limit.
- **Fonts:** a browser has no system fonts. `Program.cs` makes bundled Inter the default and
  falls back to the bundled `Assets/Fonts/NotoSansHebrew.ttf` (OFL), or Hebrew renders as boxes.
- **Trimming:** the WASM publish trims (`TrimMode=partial`, `SharpKml.Core` rooted); the same
  source-gen JSON rule applies.
- **Deploy:** `.github/workflows/web.yml` builds with the `wasm-tools` workload and uploads
  `publish/browser/wwwroot` with `vercel deploy` (main → production, other branches → preview).
  Needs repo secrets `VERCEL_TOKEN`, `VERCEL_ORG_ID`, `VERCEL_PROJECT_ID`. The Vercel project
  must **not** be connected to the Git repository — Vercel's own build can't compile WASM.
```

- [ ] **Step 3: Record the deploy deviation in the spec**

In `docs/superpowers/specs/2026-09-15-cloud-hosting-design.md`, `## Deployment` section, replace the bullet that starts with `` `vercel.json`: `installCommand` runs `dotnet-install.sh` `` (the whole bullet) with:

```markdown
- The WASM bundle is built in GitHub Actions (`.github/workflows/web.yml`, `wasm-tools`
  workload), not on Vercel — Vercel's build image has no emscripten/workload support. The
  workflow uploads the static `wwwroot` with `vercel deploy` (main → production, branches →
  preview). Phase 4 adds the `api/` Node functions and the daily blob-cleanup cron to the
  same deploy.
```

In `## Build order`, append to item 2 (after its existing text): ` Deployed by GitHub Actions + Vercel CLI (see Deployment).`

Read both sections first so the replacements match exactly.

- [ ] **Step 4: Verify and commit**

Run: `dotnet test tests/GmapPlanner.Core.Tests` — Expected: 104 passed, 0 failed.
Run: `git diff --stat` — Expected: only `.github/workflows/web.yml` (new), `CLAUDE.md`, the spec.

```bash
git add .github/workflows/web.yml CLAUDE.md docs/superpowers/specs/2026-09-15-cloud-hosting-design.md
git status --short
git commit -m "Deploy the browser host to Vercel from GitHub Actions; document the web host

Co-Authored-By: Claude Opus 5 <noreply@anthropic.com>"
```

- [ ] **Step 5: First live deploy (controller + human; not an implementer step)**

This step publishes to the internet, so the controller does it with the user, one confirmation at a time.

1. **The user creates the Vercel project, without Git.** In a terminal on their own machine:
   - `npx vercel login`
   - `npx vercel project add my-maps-generator`

   Don't use "Import Git Repository" in the dashboard: a Git-connected project tries to build on every push and fails.
2. **The user collects the three values:**
   - `VERCEL_TOKEN`: vercel.com → Account Settings → Tokens → Create.
   - `VERCEL_ORG_ID`: Account Settings → General → "Vercel ID" (personal account) or Team Settings → General → "Team ID".
   - `VERCEL_PROJECT_ID`: the project → Settings → General → "Project ID".
3. **The user adds the secrets.** In GitHub, go to `BenDayan123/map-generator` → Settings → Secrets and variables → Actions → New repository secret, and add all three. They must not paste any of these values into the chat.
4. **The controller asks the user for explicit permission** to push `feature/cloud-hosting` to `origin`. Pushing makes the branch public in the public repository and triggers the deploy.
5. **The controller pushes:** `git push -u origin feature/cloud-hosting`.
6. **Watch the run:**
   - Run `gh run list --workflow web.yml --branch feature/cloud-hosting --limit 1`.
   - Then `gh run watch <run-id> --exit-status`.
   - On success, read the preview URL: `gh run view <run-id> --log | grep "Deployed:"`.
7. **The user runs the Task 4 Step 7 checklist (items 1–7) against the preview URL**, not localhost. Places API cross-origin success (item 4) and Hebrew rendering are the two checks that matter most on the real host.
