# CLAUDE.md

Guidance for Claude Code when working in this repository.

## What this is

A C#/.NET rewrite of [Google Map Planner](../Google%20Map%20Planner) (Python/Streamlit),
targeting a small, self-contained cross-platform desktop app instead of a ~500MB
PyInstaller/Streamlit bundle. Avalonia UI (MVVM), .NET 8 LTS, `win-x64` + `osx-arm64`.

**Phase 1 (done):** itinerary extraction (Gemini) -> geocoding -> KML generation, with a
desktop UI. **Phase 2 (done):** My Maps publishing (Playwright) and Drive sharing.
**Not ported:** Sheets analytics (`analytics.py`), the in-app updater (`updater.py`), and
the hosted/headless `GOOGLE_STORAGE_STATE` path — this is a desktop app with a real
browser and an interactive login, and Google re-challenges replayed sessions anyway.

## Solution structure

```
GmapPlanner.sln
src/
  GmapPlanner.Core/          # no UI references — models, services, prompt
    Models/                  # Trip, Day, Location
    Errors/                  # PipelineException, BadResponseException
    Prompt/                  # ExtractionPrompt (ported verbatim from prompt.py)
    Services/
      Gemini/                # GeminiExtractionService (HttpClient, no SDK)
      GeocodingService.cs
      KmlBuilder.cs          # SharpKml.Core
      PipelineService.cs     # orchestrates extract -> geocode -> write KML
      Publish/               # phase 2
        MyMapsSession.cs     # Playwright automation of the My Maps editor
        MyMapsSelectors.cs   # the selectors Google keeps breaking
        MyMapsImport.cs      # the import retry, behind IImportSurface so it's testable
        DriveShareService.cs # OAuth, permissions.create, copyRequiresWriterPermission
        PublishService.cs    # one map per KML file, then share
    AppConfig.cs              # GEMINI_MODEL, MAX_LAYERS_PER_FILE, DAY_COLORS, ...
    AppDataPaths.cs           # per-OS app data dir (ports paths.py)
  GmapPlanner.App/            # Avalonia MVVM desktop app
    ViewModels/MainViewModel.cs
    Views/MainWindow.axaml(.cs)
tests/
  GmapPlanner.Core.Tests/     # xunit
```

No DI container, no interfaces with a single implementation — services are constructed
directly. Keep it that way unless a second implementation actually shows up.

## Build / run / test

```bash
dotnet build
dotnet test
dotnet run --project src/GmapPlanner.App
```

Publish (small, self-contained, single file):

```bash
dotnet publish src/GmapPlanner.App -c Release -r win-x64
dotnet publish src/GmapPlanner.App -c Release -r osx-arm64
```

`GmapPlanner.App.csproj` sets `PublishSingleFile`, `SelfContained`, `PublishTrimmed`
(`TrimMode=partial`). `InvariantGlobalization` is on for size; revisit if Hebrew text
sorting/formatting (not rendering — that's unaffected) ever needs real culture data.

## Trimming rules — read before adding reflection-based code

Trimming is not just a publish-time size knob here; it changes runtime behaviour, and
it already caused one crash (picking an output folder killed the whole app).

1. **Never call reflection-based `System.Text.Json` APIs.** `PublishTrimmed` sets the
   `JsonSerializerIsReflectionEnabledByDefault=false` feature switch, which lands in
   **every** build, Debug included — so `JsonSerializer.Serialize(obj)`,
   `ReadFromJsonAsync<T>()`, `JsonContent.Create(new { … })` all throw
   `InvalidOperationException` at runtime. Add the type to
   `Core/Json/GmapPlannerJsonContext.cs` and pass its `JsonTypeInfo` instead.
   The test project turns the same switch off, so a reflection-based call fails
   `dotnet test` rather than reaching a user.
2. **Keep XAML bindings compiled.** `MainWindow.axaml` sets `x:CompileBindings="True"`
   with `x:DataType`. Reflection bindings survive a Debug run and can break only once
   trimmed — exactly the failure mode that is hardest to notice.
3. **Reflection-heavy dependencies need a trimmer root.** `SharpKml.Core` serializes
   via reflection over its own DOM, so it is listed as a `TrimmerRootAssembly`. Do the
   same for `Google.Apis`/Playwright in Phase 2.
4. **Verify against the published exe, not just `dotnet run`.** A clean publish should
   emit no `IL2026` warnings from our own code; the ones left are Avalonia's designer
   and remote-protocol assemblies, which aren't used at runtime.

## UI

`MainWindow.axaml` mirrors the original Streamlit app: a left sidebar (nav, Options —
days-per-KML slider and skip-geocoding toggle) and a main pane with the drag & drop
itinerary zone, "Generate map files", progress, an error banner, and the results block
(success line, Days / Locations / Exact coords metric cards, per-file cards, and the
import instructions). A second "page" holds the API keys and output folder; the two
pages are `IsVisible` toggles on `IsMakeMapPage`/`IsSettingsPage`, not a nav framework.

Everything is MVVM except the file/folder pickers and drag & drop, which need the
`TopLevel`'s `StorageProvider` and so live in `MainWindow.axaml.cs`. Those handlers are
`async void`, so they route through `SafeAsync` — an exception in one would otherwise
take the process down instead of showing up in the error banner.

## Key ports from the Python original

- **`GeminiExtractionService`** — calls `generateContent` directly via `HttpClient`
  against `generativelanguage.googleapis.com` (no SDK dependency — none of the
  "official" Gemini SDKs are actually .NET-first). Mirrors `gemini.py`: JSON-schema
  constrained output, retry-once on an unusable body (`BadResponseException`), no
  retry on a failed request (plain `PipelineException`). `.txt` goes in inline; other
  files go through the Files API resumable-upload flow (currently just `.pdf`).
- **`GeocodingService`** — mirrors `geocode.py`'s fatal-vs-recoverable status split:
  `REQUEST_DENIED`/`OVER_QUERY_LIMIT`/`OVER_DAILY_LIMIT` abort the whole itinerary
  (keeping Gemini's coordinates for whatever's left); anything else falls back
  per-location.
- **`KmlBuilder`** — uses `SharpKml.Core` instead of hand-rolled XML. Same pin-icon
  URL scheme (`mt.google.com/vt/icon`, 3-layer stack, `psize` shrinks as digits grow),
  same per-day colors (`AppConfig.DayColors`), same `{first}.kml` / `{first}-{last}.kml`
  naming.
- **`AppSettingsService`** / **`AppDataPaths`** — port `appconfig.py` / `paths.py`'s
  per-OS data directory + `config.json`, trimmed to the two API keys and output dir
  this phase actually uses.

## Publishing to My Maps (phase 2)

- **`MyMapsSession`** — ports `mymaps.py`. My Maps has no create/import API, so the
  editor UI is automated. The selectors in `MyMapsSelectors` are the fragile part;
  the editor is always opened with `hl=en` so the English patterns hold. Keep the
  hard-won guards if you touch this: the persistent Chromium profile (one-time headed
  login), the real Chrome/Edge channel with `--enable-automation` dropped (Google
  blocks sign-in otherwise), preferring the Picker's own frames when setting the file
  input (a wrong input imports nothing and silently leaves the map empty), nudging the
  Picker's "Upload" tab on 2nd+ imports, treating the "action was reverted" toast as an
  immediate abort plus editor reload, `MapGapSeconds` between maps, verifying the import
  by waiting for the KML's first placemark to render, and renaming via the *current* name
  read from the tab title rather than the literal "Untitled map" (importing a KML makes
  My Maps rename the map after the file, so that text is usually already gone).
- **`MyMapsImport`** — the click → set-file → dialog-closes retry, extracted behind
  `IImportSurface` purely so it can be tested without a browser. This is the path that
  made publishing flaky, so it has real tests; keep them passing.
- **`DriveShareService`** — ports `drive_share.py`. A My Maps map is a Drive file, so
  sharing goes through `permissions.create` rather than the brittle share dialog.
  `RestrictDownloadAsync` sets `copyRequiresWriterPermission`, which is the API form of
  Share → gear → "Commenters and viewers" under *download, print, and copy*; it is
  applied to every created map and is best-effort, so a failure never loses a map.
  Needs an OAuth **Desktop** client saved as `credentials.json` in the app data dir;
  the token is cached beside it. Drive auth runs up front so a bad setup fails before
  the browser work — but with no recipients it stays optional.

The browser is deliberately **not** bundled (that's what keeps the download reasonable);
Playwright fetches Chromium on first publish. Playwright's own node driver *is* bundled
and costs ~100MB — the app is ~230MB (win-x64) / ~290MB (osx-arm64) because of it.
Swapping to PuppeteerSharp would bring it back to ~48MB at the cost of reimplementing
the role/text selector helpers.

### Playwright's node driver is per-platform — three traps, all handled in the csprojs

The driver ships as `.playwright/node/<platform>/` and Playwright execs it as a real
file, which fights every default here. Publishing `osx-arm64` from a Windows box got
this wrong three ways before the fixes in `GmapPlanner.Core.csproj` / `GmapPlanner.App.csproj`:

1. **Library leaks the host driver.** `GmapPlanner.Core` holds the `Microsoft.Playwright`
   PackageReference but builds RID-agnostic, so `Microsoft.Playwright.targets` resolved
   the driver off the *build host* (win32_x64) and it rode into the mac publish. Core sets
   `<PlaywrightPlatform>none</PlaywrightPlatform>` — a library bundles no driver; the app does.
2. **App must map its RID.** `GmapPlanner.App` sets `PlaywrightPlatform` = `osx-arm64` /
   `win` from `$(RuntimeIdentifier)` so its build output holds the one correct driver.
   Left empty (a dev `dotnet run`) it falls through to the host driver, which is right for
   a local run.
3. **Single-file strips the driver.** `PublishSingleFile` drops the loose
   `node/<platform>` folder from the publish dir (the bundler swallows the native node
   exe), but Playwright needs it on disk. The `RestorePlaywrightNodeDriver` target copies
   it back next to the exe after publish, and re-adds the `+x` bit on a non-Windows host.

Net: `win-x64` publish ships only `win32_x64`, `osx-arm64` ships only `darwin-arm64`, no
cross-contamination. Verify a driver change by listing `publish/.playwright/node/` — it
must contain exactly the target platform's folder plus `LICENSE`.

## Not ported (yet)

Streamlit UI (`streamlit_app.py`, `pages/`) and the pywebview desktop wrapper — Avalonia
*is* the native wrapper, so neither has an equivalent here. Still open from the original
repo: Sheets analytics (`analytics.py`) and the in-app updater (`updater.py`).
