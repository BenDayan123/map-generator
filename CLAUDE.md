# CLAUDE.md

Guidance for Claude Code when working in this repository.

## What this is

A C#/.NET rewrite of [Google Map Planner](../Google%20Map%20Planner) (Python/Streamlit),
targeting a small, self-contained cross-platform desktop app instead of a ~500MB
PyInstaller/Streamlit bundle. Avalonia UI (MVVM), .NET 8 LTS, `win-x64` + `osx-arm64`.

**Phase 1 (this codebase, done):** itinerary extraction (Gemini) -> geocoding -> KML
generation, with a desktop UI to run it. **Phase 2 (not started):** Playwright-driven
My Maps automation, Drive sharing, Sheets analytics — ported from `mymaps.py`,
`drive_share.py`, `analytics.py` in the original repo when picked up.

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
(`TrimMode=partial` — full trim mode is worth revisiting once Phase 2 adds
`Google.Apis`/Playwright, which are reflection-heavy and may need explicit trimmer
root exceptions). `InvariantGlobalization` is on for size; revisit if Hebrew text
sorting/formatting (not rendering — that's unaffected) ever needs real culture data.

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

## Not ported (yet)

Streamlit UI (`streamlit_app.py`, `pages/`), the pywebview desktop wrapper
(Avalonia *is* the native wrapper), Playwright My Maps automation, Drive sharing,
Sheets analytics, the in-app updater. Pick these up from the original repo's
`gmap_planner/mymaps.py`, `drive_share.py`, `analytics.py`, `updater.py` when Phase 2
starts — each should get its own design pass (Playwright automation especially:
its browser dependency doesn't shrink no matter the host language).
