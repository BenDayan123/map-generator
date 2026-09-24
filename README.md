# Google Map Planner

Desktop app that turns a raw itinerary (text, PDF, or an image) into ready-to-import Google
My Maps KML files — extraction, geocoding, and map generation in one flow, with optional
one-click publishing straight to My Maps.

C#/.NET rewrite of the original Python/Streamlit tool, built as a small, self-contained
cross-platform desktop app.

## What it does

1. **Extract** — drop in an itinerary (`.txt`, `.pdf`, or paste text) and Gemini pulls out
   days, places, and notes as structured JSON.
2. **Geocode** — each place is resolved to coordinates via Google Places API (New) Text Search.
3. **Generate** — a KML file per map (days are split across files by a configurable
   days-per-map limit), with per-day colors and numbered pins.
4. **Publish (optional)** — imports each KML straight into Google My Maps via a real browser
   (Playwright), then shares the map through Drive with view/comment-only permissions.
5. **Track (optional)** — every generated trip is logged to a Google Sheet, and the
   in-app Analytics page reads it back (trip counts, map counts, place counts, this month).

## Install

Download the latest version from the
[Releases page](https://github.com/BenDayan123/map-generator/releases/latest).

### Windows

Run **MyMapsGenerator-Setup-win-x64.exe**. If SmartScreen warns, click **More info → Run anyway**.

### macOS (Apple Silicon)

**Before you start**

1. Check your Mac has an Apple chip: Apple menu  → **About This Mac** — "Chip" must say
   **Apple M1, M2, M3 or M4** (Intel Macs aren't supported).
2. Use an **administrator** account (the installer copies the app into Applications).
3. Install **Google Chrome** (or Microsoft Edge) — needed only to publish to Google My Maps,
   since the Google sign-in runs through it.

**Install (about 1 minute, no security warnings)**

4. Open **Terminal** (⌘ Space → type `Terminal` → Return).
5. Paste this line and press Return:

   ```bash
   curl -fsSL https://raw.githubusercontent.com/BenDayan123/map-generator/main/build/install-macos.sh | bash
   ```

6. It downloads the latest version (~300 MB), installs **My Maps Generator** into
   Applications, and opens it. You can close Terminal.
7. From now on open it like any app — from Applications or ⌘ Space → "My Maps Generator".

**First-time setup inside the app**

8. **Settings** → enter your API keys (or drop in your one-file setup `.json`).
9. To publish to My Maps: turn on **Create my maps online** → **Sign in to Google** → sign
   in once in the Chrome window that opens.
10. The first publish may take an extra minute while it downloads a browser component (~150 MB).

**Updating:** Settings → **Check for updates** → **Install** — the app closes, updates itself,
and reopens. Re-running the Terminal line also reinstalls the latest version.

**If something goes wrong**

- *"is damaged and can't be opened"* — that's an old v1.0.x download. Trash it and repeat step 5.
- *The Terminal can't write to Applications* — switch to an admin account and repeat step 5.
- *The app closes right away* — Finder → Go → Go to Folder… →
  `~/Library/Application Support/GmapPlanner/` and send the `last_error.log` file there.

Prefer the `.dmg` from the Releases page? It works too, but because the app is free and not
registered with Apple, macOS blocks the first launch: go to **System Settings → Privacy &
Security → Open Anyway**. Full guide: [docs/macos-install.md](docs/macos-install.md).

## Stack

- **.NET 8**, **Avalonia UI** (MVVM, compiled bindings)
- **Gemini** (`generateContent` REST, JSON-schema constrained output) for extraction
- **Google Places API (New)** (Text Search) for coordinates
- **SharpKml** for KML generation
- **Playwright** for My Maps automation (browser not bundled — fetched on first publish)
- **Google Drive API** for sharing, **Google Sheets API** for analytics
- Self-contained, trimmed publish: single-file `win-x64`, ad-hoc-signed `.app` for `osx-arm64`

## Getting started

```bash
dotnet build
dotnet test
dotnet run --project src/GmapPlanner.App
```

On first run, open **Settings** and provide:
- a Gemini API key
- a Google API key with Places API (New) enabled
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
