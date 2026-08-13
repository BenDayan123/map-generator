# Google Map Planner

Desktop app that turns a raw itinerary (text, PDF, or an image) into ready-to-import Google
My Maps KML files — extraction, geocoding, and map generation in one flow, with optional
one-click publishing straight to My Maps.

C#/.NET rewrite of the original Python/Streamlit tool, built as a small, self-contained
cross-platform desktop app.

## What it does

1. **Extract** — drop in an itinerary (`.txt`, `.pdf`, or paste text) and Gemini pulls out
   days, places, and notes as structured JSON.
2. **Geocode** — each place is resolved to coordinates via the Google Geocoding API.
3. **Generate** — a KML file per map (days are split across files by a configurable
   days-per-map limit), with per-day colors and numbered pins.
4. **Publish (optional)** — imports each KML straight into Google My Maps via a real browser
   (Playwright), then shares the map through Drive with view/comment-only permissions.
5. **Track (optional)** — every generated trip is logged to a Google Sheet, and the
   in-app Analytics page reads it back (trip counts, map counts, place counts, this month).

## Stack

- **.NET 8**, **Avalonia UI** (MVVM, compiled bindings)
- **Gemini** (`generateContent` REST, JSON-schema constrained output) for extraction
- **Google Geocoding API** for coordinates
- **SharpKml** for KML generation
- **Playwright** for My Maps automation (browser not bundled — fetched on first publish)
- **Google Drive API** for sharing, **Google Sheets API** for analytics
- Self-contained, trimmed, single-file publish for `win-x64` and `osx-arm64`

## Getting started

```bash
dotnet build
dotnet test
dotnet run --project src/GmapPlanner.App
```

On first run, open **Settings** and provide:
- a Gemini API key
- a Google Geocoding API key
- an output folder for generated KML files

Optional, for extra features:
- a Google Cloud service-account JSON (usage ring + Sheets analytics)
- an `ANALYTICS_SHEET_ID` (Sheet must be shared as Editor with the service account)
- a Drive OAuth `credentials.json` (for sharing published maps)

A single JSON "setup bundle" can fill in all of the above at once from the Settings page.

## Publishing a build

```bash
dotnet publish src/GmapPlanner.App -c Release -r win-x64
dotnet publish src/GmapPlanner.App -c Release -r osx-arm64
```

Tagged pushes (`vX.Y.Z`) trigger `.github/workflows/release.yml`, which builds both
platforms and attaches a Windows installer and a macOS `.dmg` to a GitHub Release.

## Project layout

```
src/
  GmapPlanner.Core/   # models, services, prompt — no UI references
  GmapPlanner.App/    # Avalonia MVVM desktop app
tests/
  GmapPlanner.Core.Tests/
```

See [CLAUDE.md](CLAUDE.md) for the full architecture writeup, including the extraction →
geocoding → KML pipeline, the My Maps automation internals, the self-update mechanism, and
the trimming rules that keep the published binary small.

## Status

Extraction, geocoding, KML generation, My Maps publishing, Drive sharing, in-app
self-update, and Sheets-backed analytics are all implemented. The original Streamlit UI
and hosted/headless publishing path are not — this is a native desktop app with a real
browser and interactive login instead.
