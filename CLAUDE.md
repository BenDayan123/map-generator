# CLAUDE.md

Guidance for Claude Code when working in this repository.

## What this is

A C#/.NET rewrite of [Google Map Planner](../Google%20Map%20Planner) (Python/Streamlit),
targeting a small, self-contained cross-platform desktop app instead of a ~500MB
PyInstaller/Streamlit bundle. Avalonia UI (MVVM), .NET 8 LTS, `win-x64` + `osx-arm64`.

**Phase 1 (done):** itinerary extraction (Gemini) -> geocoding -> KML generation, with a
desktop UI. **Phase 2 (done):** My Maps publishing (Playwright) and Drive sharing.
**Phase 3 (done):** in-app self-update via GitHub Releases (`UpdateService`) plus the
`win-x64` Inno installer / `osx-arm64` `.dmg` built by `.github/workflows/release.yml`.
**Phase 4 (done):** Sheets analytics (`SheetsAnalyticsService`) — the Analytics page logs
each generated trip to a Google Sheet and reads it back, porting `analytics.py`.
**Not ported:** the hosted/headless `GOOGLE_STORAGE_STATE` path — this is a desktop app
with a real browser and an interactive login, and Google re-challenges replayed sessions anyway.

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

`MainWindow.axaml` mirrors the original Streamlit app: a left sidebar (nav, the live
geocoding-usage ring, Options — days-per-KML slider and skip-geocoding toggle, and the
Publish to My Maps controls) and a main pane with the drag & drop itinerary zone,
"Generate map files", progress, an error banner, and the results block (success line,
Days / Locations / Exact coords metric cards, per-file cards, and the import
instructions). A second "page" is the Settings page — one-file setup, API keys,
output folder, the service-account JSON for the usage ring, a credentials.json picker,
and a green/⚪ setup-status checklist. The two pages are `IsVisible` toggles on
`IsMakeMapPage`/`IsSettingsPage`, not a nav framework.

**Languages (English / Hebrew).** A button at the top of the sidebar toggles `MainViewModel.IsHebrew`, which
flips the window's `FlowDirection` to RTL and is saved as `AppSettings.Language`. Every UI string
lives in `Localization/Strings.cs` (key → English, Hebrew); XAML uses `{l:T Key}` and code uses
`Loc.T` / `Loc.F`. `{l:T}` binds through an `IObservable` (`ToBinding()`), not a reflection
binding, so it stays trim-safe (rule #2). Core's English progress lines are mapped in
`Loc.Progress`; exception messages from Core/Google stay English. Gotcha: a centered, auto-width
`TextBlock` with wrapping mis-measures RTL text and hides its last word — use `NoWrap` there.

The usage ring is drawn by hand (`UsageRing.ArcGeometry` builds an SVG-style arc string,
the view wraps it in a `Geometry` over a `Canvas` so the track ellipse and progress arc
share one absolute coordinate space); a `Viewbox` shrinks it to a small ring with the
percent inside, and the Places API used/limit and reset countdown sit to its right on the
same row. It stays hidden until a service-account JSON is set and Cloud Monitoring
answers — every failure just hides it, never errors.

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
- **Trip name prompt** — `PipelineService.RunAsync(confirmTripName:)` builds the Gemini document
  part once (`BuildDocumentPartAsync`, one PDF upload), then runs `SuggestTripNameAsync` (Hebrew name
  from the file name if meaningful, else the body; falls back to the file stem) alongside extraction.
  The app shows it in a right-side panel (`IsNamePromptOpen`); KML writing and publishing wait for
  Approve. Map titles are `"<name> (ימים X-Y)"` / `"<name> (יום X)"` (`PublishService.TitleFor`).
- **`GeocodingService`** — now calls **Places API (New)** Text Search (`POST places:searchText`,
  `X-Goog-FieldMask: places.displayName,places.id,places.location`) instead of the Geocoding
  API, which is an address geocoder and mis-pinned named POIs (wrong city / unrelated shop).
  It asks for 5 English candidates biased (50 km circle) to Gemini's own coordinates and
  overrides Google's first hit only when a candidate's name tokens exactly match the name
  before ", City" (`PickBest`; anything looser let near-misses beat a correct first hit) —
  Google's first hit is often a more *popular* place whose name merely contains the
  query ("Nike Shibuya Scramble Square" for "Nike Shibuya").
  Keeps `geocode.py`'s fatal-vs-recoverable split (fatal = `PERMISSION_DENIED`/`RESOURCE_EXHAUSTED`/
  `UNAUTHENTICATED` or a bad key) aborts the whole itinerary (keeping Gemini's coordinates
  for whatever's left); anything else, including no match, falls back per-location.
- **`KmlBuilder`** — uses `SharpKml.Core` instead of hand-rolled XML. Same pin-icon
  URL scheme (`mt.google.com/vt/icon`, 3-layer stack, `psize` shrinks as digits grow),
  same per-day colors (`AppConfig.DayColors`), same `{first}.kml` / `{first}-{last}.kml`
  naming.
- **`AppSettingsService`** / **`AppDataPaths`** — port `appconfig.py` / `paths.py`'s
  per-OS data directory + `config.json` (Gemini/Geocoding keys, output dir, and the
  service-account JSON for the usage ring).
- **`SetupBundleService`** — ports `appconfig.py`'s `apply_setup_bundle`: one JSON that
  fills the keys and writes the Drive `credentials.json` (from a `credentials` key).
  Missing/blank keys keep their current value; a non-JSON `credentials` is skipped, not
  written. This is the one-drop fix for "credentials.json not found" when sharing.
- **`UsageService`** — ports `usage.py`: a service-account token (via
  `GoogleCredential`, no monitoring SDK) plus a raw Cloud Monitoring `timeSeries` query
  for `places.googleapis.com` request_count this month → percent of
  `GeoMonthlyLimit` (10,000). Best-effort; returns null (ring hidden) on any failure.

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
  The import dialog is the classic Google Picker (`docs.google.com/picker`, title
  "Choose a file to import"): it opens on the **My Drive** tab, and the `<input type=file>`
  only appears after the **Upload** tab is clicked — inside a nested "scotty" upload iframe
  whose URL is *not* docs/drive/picker. So the file search must scan **every** frame, not
  just Picker frames (`OrderedFrames`), or the input is never found and the run hangs on
  the overlay. And `PickerOpen` (the "still waiting for a file" signal) must match only the
  Upload pane's transient "drag and drop" text — never the picker's persistent
  "Choose a file to import" title, or the dialog looks open forever and every import fails.
  One more Picker variant imports the file but **never auto-closes** — it resets to the
  drag view and lingers. So success is *not* "the dialog closed": `WaitForPickerClose`
  waits for the KML's first placemark to render in the editor (`IsImportedAsync`) as the
  real signal — a closed/flickering dialog on its own is a false positive (the Upload pane
  briefly hides its drag-text mid-upload) that reports done seconds early and drops the
  file. The lingering Picker is then dismissed with Escape so it doesn't block the rename.
  Speed: the input is set ~900ms *after* the Upload tab opens (setting it the instant it
  appears races the uploader's init and the file drops), and the dropped-upload retry
  timeout is short (`closeTimeoutMs`) since a real import renders in ~4s — a healthy import
  is ~4s, not the ~30s a premature-success-then-timeout used to cost.
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

`LaunchPersistentAsync` prefers the installed **Chrome → Edge → bundled Chromium** in that
order (`_launchedChannel` records which won). Google blocks sign-in inside bundled Chromium,
so a Mac with neither Chrome nor Edge can *publish* headlessly once logged in, but **login
itself needs a real Chrome/Edge** — `LoginAsync` detects the bundled-Chromium fallback
(`OnBundledChromium`) and throws an "install Chrome" message instead of a bare timeout. Two
more macOS guards live in `MyMapsSession`: the synchronous `EnsureDriverInstalled()` (which
may download ~150MB on first publish) runs inside `Task.Run` so it never freezes the UI
thread, and `StripQuarantineMac()` clears `com.apple.quarantine` off the bundled `.playwright`
node driver at startup (the unsigned `.dmg` quarantines it, which would otherwise block the
`node` binary Playwright execs).

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

## Releasing (phase 3)

- **Version** lives in `GmapPlanner.App.csproj` (`<Version>`). CI stamps the release tag
  over it (`-p:Version=x.y.z`) so the built binary and the tag always agree; the updater
  reads it via `Assembly.GetEntryAssembly().GetName().Version`.
- **`UpdateService`** ports `updater.py`: query the repo's `releases/latest`, compare tags
  (`IsNewer`), pick this OS's asset (`.exe` on Windows, arm64 `.dmg` on macOS), download to
  temp, and apply — Windows runs the Inno installer `/SILENT` then `Environment.Exit`s so
  the files free up (installer relaunches via `installer.iss` `[Run] Check:WizardSilent`);
  macOS `open`s the `.dmg` for a drag-install. All best-effort: any failure returns null so
  a missing connection never breaks the app. The GitHub API JSON goes through the
  source-gen `GmapPlannerJsonContext` (trimming rule #1), never reflection. The check is
  **manual only** (Settings → "Check for updates"); there is no startup poll.
- **Packaging** is per-platform, built by tag push (`v*`) in `.github/workflows/release.yml`:
  `windows-latest` publishes `win-x64` and compiles `build/installer.iss` with Inno Setup
  (per-user, no UAC) → `MyMapsGenerator-Setup-win-x64.exe`; `macos-14` (arm64) publishes
  `osx-arm64` and `build/make-macos-dmg.sh` wraps it into a `.app` (+ `.playwright` driver,
  `chmod +x`) and an `hdiutil` `.dmg`. A `release` job attaches both to the GitHub Release.
  The macOS app is **unsigned** — first launch needs a right-click → Open past Gatekeeper.
- **Cutting a release:** merge to `main`, then `git tag v1.2.3 && git push origin v1.2.3`.

## Not ported (yet)

Streamlit UI (`streamlit_app.py`, `pages/`) and the pywebview desktop wrapper — Avalonia
*is* the native wrapper, so neither has an equivalent here. Nothing else from the original
repo is outstanding.

## Analytics Sheet (phase 4)

- **`SheetsAnalyticsService`** — ports `analytics.py`. Each generated trip is appended to a
  Google Sheet (cols `Created At / Trip Name / Maps / Places / Map Links`, A..E) and the
  Analytics page reads it back. Auth reuses the usage-gauge service-account JSON
  (`AppSettings.GcpSaJson`) scoped to `spreadsheets`; the Sheet id (bare or full URL) is
  `AppSettings.AnalyticsSheetId`, set on the Settings page and carried by the one-file setup
  bundle (`ANALYTICS_SHEET_ID`). Raw Sheets REST + `JsonNode` (no Sheets SDK, no source-gen
  DTOs — DOM `JsonObject`/`JsonArray` bodies dodge trimming rule #1). `EnsureLayoutAsync`
  ports `_layout_requests`/`_SUMMARY`: frozen teal header, banded rows, real datetimes in A,
  a summary box of live formulas (`=SUMIFS`/`COUNTUNIQUE`/`EOMONTH` month windows). All
  best-effort: `FetchRowsAsync` returns null (page shows a "configure it" message) on any
  failure, `RecordPublishAsync` swallows so logging never breaks a run. The Sheet must be
  shared (Editor) with the SA email and the Sheets API enabled. No hosted Sheet fallback —
  a local `analytics.json` is *not* kept; the Sheet is the single source.
