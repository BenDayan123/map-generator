# Cloud hosting — design

Date: 2026-09-15 · Branch: `feature/cloud-hosting` · Status: approved design, pending spec review

## Goal

Run the app as a website on Vercel for a few trusted groups, with My Maps publishing
executed in the cloud by GitHub Actions. The desktop app keeps working unchanged.

## Decisions

| Topic | Decision |
|---|---|
| Users | Me + a few trusted groups; each group has its own API keys and Google account |
| Frontend | Avalonia UI compiled to WebAssembly (`Avalonia.Browser`), static on Vercel |
| Site auth | None. No accounts, no server-side persistent secrets |
| Secret storage | Browser only (localStorage/IndexedDB): Gemini key, Places key, SA JSON, Sheet id, `session.json` |
| Generation | Client-side in the browser (Gemini → Places → KML), reusing `GmapPlanner.Core` |
| Publishing | Per-job GitHub Actions run in a **private** worker repo, reusing the C# publish code |
| Google login | Local login helper exe produces `session.json`; user drops it into web Settings |
| Kept features | Drive sharing, usage ring, Analytics Sheet |
| Docker | Not needed. Fallback only: worker container on Cloud Run Jobs if Actions is flagged/slow |

## Architecture

```
Browser (Avalonia WASM, static on Vercel)
  ├─ Settings: keys, SA JSON, Sheet id, session.json → localStorage/IndexedDB
  ├─ Generate: Gemini → Places → KML, client-side (Core)
  └─ Publish / usage / analytics → Vercel API
          │
Vercel API (TypeScript functions, stateless)
  ├─ POST /api/jobs              encrypt payload → Blob jobs/<uuid>, status/<uuid>=queued, dispatch workflow(id)
  ├─ POST /api/jobs/:id/claim    worker (HMAC) → decrypted payload, deletes jobs/<uuid>
  ├─ POST /api/jobs/:id/status   worker (HMAC) → progress / done / failed (+ encrypted refreshed session)
  ├─ GET  /api/jobs/:id          browser polls; final read returns result + refreshed session, deletes status blob
  ├─ POST /api/usage             SA JSON in body → Cloud Monitoring → gauge
  ├─ POST /api/analytics         SA JSON + Sheet id in body → Sheets read
  └─ cron (daily)                delete jobs/ and status/ blobs older than 1h
          │ workflow_dispatch (input: job id only)
GitHub Actions — private worker repo (workflow + secrets only)
  └─ checks out this repo's branch, runs GmapPlanner.Worker:
       claim → MyMapsSession (storage-state mode) → DriveShareService → SheetsAnalyticsService.RecordPublishAsync
       → status callbacks → done/failed
```

## Components

### `GmapPlanner.Core` (split)
- **`GmapPlanner.Core`** — browser-safe: Models, Errors, Prompt, Gemini, `GeocodingService`
  (Places API (New)), `KmlBuilder`, `PipelineService`, `GmapPlannerJsonContext`. No Playwright,
  no Google.Apis references.
- **`GmapPlanner.Core.Publish`** — `MyMapsSession`, `MyMapsSelectors`, `MyMapsImport`,
  `DriveShareService`, `PublishService`, `UsageService`, `SheetsAnalyticsService`, `UpdateService`.
  Holds the Playwright and Google.Apis package references.
- `PipelineService` must stop writing KML to disk directly: it returns KML content in memory;
  desktop writes files, browser offers downloads.
- `MyMapsSession` gains a storage-state launch option (new context from a Playwright storage
  state) next to the existing persistent-profile launch, and exposes the context's refreshed
  storage state after a run. It reports `SESSION_EXPIRED` when the editor redirects to Google sign-in.

### UI projects
- **`GmapPlanner.UI`** (shared) — `MainView` (`UserControl`, current `MainWindow` content),
  view models, converters, resources. File pickers / drag & drop go through `TopLevel.StorageProvider`
  so they work in both hosts.
- **`GmapPlanner.App`** (desktop host, path and assembly name unchanged so packaging is untouched) —
  current `Window` host, unchanged behaviour, owns `PublishTrimmed`
  single-file publish and the Playwright driver targets moved from today's App csproj.
- **`GmapPlanner.App.Browser`** — WASM host (`ISingleViewApplicationLifetime`). Replaces:
  output folder → per-file Download buttons; local publish → cloud job; desktop-only Settings
  (credentials.json picker, update check, output folder) hidden; `session.json` drop zone added.
  Settings persist to localStorage/IndexedDB instead of `config.json`.

### `GmapPlanner.LoginHelper`
Small .NET console exe (win-x64, osx-arm64). Opens real Chrome/Edge via the existing
`MyMapsSession.LoginAsync`, runs the Drive OAuth consent via `DriveShareService` (user picks
their `credentials.json`), then writes one `session.json`:

```json
{
  "version": 1,
  "storageState": { "...": "Playwright storage state" },
  "driveCredentials": { "...": "OAuth Desktop client credentials.json" },
  "driveToken": { "...": "Google.Apis FileDataStore token" }
}
```

### `GmapPlanner.Worker`
.NET console app referencing `Core.Publish`. Reads `JOB_ID`, `API_BASE_URL`, `WORKER_HMAC`
from env. Writes KMLs, credentials and token to a temp dir, publishes headless on the runner's
Chrome, shares, logs analytics (if SA JSON + Sheet id present), posts status, and deletes the
temp dir on exit.

### `web/api/` (Vercel, TypeScript)
Functions listed in Architecture. Shared module: AES-256-GCM encrypt/decrypt (`JOB_KEY`),
HMAC-SHA256 verify (`WORKER_HMAC`), Blob helpers. `/api/usage` and `/api/analytics` are short
TS ports of `UsageService` (Monitoring `timeSeries` query for `places.googleapis.com`) and the
read side of `SheetsAnalyticsService`; they sign the SA JWT with Node crypto.

### Worker workflow (private repo)
`workflow_dispatch` with input `job_id`; `runs-on: ubuntu-latest`; `timeout-minutes: 20`;
`concurrency` not restricted. Steps: checkout this repo at `feature/cloud-hosting` (later `main`),
setup .NET 8, `playwright install chromium` fallback, run Worker, `if: failure()` step posts
`failed` status via curl + HMAC.

## Data flow

### Generate (browser)
1. User picks/drops an itinerary; bytes read through the storage provider.
2. `GeminiExtractionService`, key from localStorage. `.txt` inline as today. PDF: verify first
   that the Files API resumable upload works under CORS (the `X-Goog-Upload-URL` response header
   must be exposed). If not, PDFs switch to Gemini `inline_data` base64 (limit 20MB) in the browser.
3. `GeocodingService` → Places API (New) `places:searchText`.
4. `KmlBuilder` → KML strings in memory; results block shows Download buttons.
5. Browser calls `/api/usage` to refresh the ring when an SA JSON is set.

### Publish (cloud job)
1. Browser `POST /api/jobs` with `{ tripName, kmls: [{name, content}], recipients, role,
   session (session.json), saJson?, sheetId? }`.
2. Vercel: encrypt with `JOB_KEY` → Blob `jobs/<uuid>`; write `status/<uuid>` `{state:"queued"}`;
   dispatch workflow with `job_id`; return `{ id }`.
3. Worker: `POST /api/jobs/:id/claim` (HMAC) → decrypted payload; Vercel deletes `jobs/<uuid>`.
4. Worker: publish loop as today → share → analytics log. Posts
   `{state:"running", message:"map 2/3"}` updates, then
   `{state:"done"|"failed", maps:[{title,url}], error?, errorCode?, refreshedSession?}`
   (Vercel encrypts `refreshedSession` into the status blob).
5. Browser polls `GET /api/jobs/:id` every 5s. On `done`/`failed`: shows map links / error,
   stores `refreshedSession` over the local `session.json`. Vercel deletes the status blob after
   this final read.

### Secrets

| Where | Holds |
|---|---|
| Browser | All user keys and `session.json` |
| Vercel env | `JOB_KEY` (32-byte), `WORKER_HMAC`, `GITHUB_TOKEN` (fine-grained, Actions:write on worker repo only), `WORKER_REPO`, `BLOB_READ_WRITE_TOKEN` |
| Worker repo secrets | `WORKER_HMAC`, `API_BASE_URL` |
| Blob store | Ciphertext only; job payloads live between dispatch and claim |

## Error handling

- Browser generation: existing `PipelineException` → error banner. CORS/network failures surface
  as a banner naming the failing API.
- No `session.json` loaded → Publish disabled with a hint to run the login helper.
- `SESSION_EXPIRED` from the worker → UI message: "Google session expired — re-run the login
  helper and drop the new session.json".
- Dispatch failure → `/api/jobs` returns 502, status set to `failed`, banner shown.
- Worker crash/timeout → workflow `if: failure()` posts `failed`; browser also treats 10 min
  without a status update as failed.
- Partial publish → maps created before the failure are included in the `failed` status.
- Orphaned blobs (lost dispatch, never-polled status) → daily cron deletes blobs older than 1h.
- Usage ring / Analytics → best-effort; any failure hides the ring / shows "configure it", as today.
- Abuse: no auth means a leaked URL can dispatch jobs and consume Actions minutes. Mitigations:
  Vercel firewall rate limit on `/api/jobs` (e.g. 10/hour per IP), payload size cap (2MB),
  max 10 KML files per job. Jobs without a valid Google session fail quickly.

## Known risks (not solvable in code)

- **Google re-challenge:** runners use datacenter IPs; Google may reject a replayed session even
  with rotated cookies. Surfaced as `SESSION_EXPIRED`.
- **GitHub Actions terms:** using Actions as a service backend is a grey area. Fallback: move the
  Worker into a Docker container (official Playwright .NET image) on Cloud Run Jobs; only the
  dispatch step in `/api/jobs` changes.
- **Avalonia.Browser maturity:** Hebrew/RTL text and text input must be verified in the manual
  checklist. First load is roughly 10–30MB.
- **Actions minutes:** private repo free tier is 2,000 min/month; each job ≈ 2–5 min.

## Testing

- Existing 88 xunit tests stay green through the Core/App split (move tests with their code;
  `MyMapsImport` tests move to a `Core.Publish` test project if needed).
- Desktop regression by hand: generate + publish + share from the desktop app.
- New unit tests:
  - TS: AES-GCM round-trip, HMAC accept/reject (one test file).
  - xunit: `session.json` parsing, worker payload parsing, `PipelineService` in-memory KML output.
- Trimming: the WASM publish is trimmed; reflection-based `System.Text.Json` stays forbidden
  (CLAUDE.md rule #1). WASM publish must emit no `IL2026` from our own code.
- Manual end-to-end checklist:
  - Generate from `.txt` and `.pdf` in Chrome, Edge, Firefox, Safari.
  - Hebrew itinerary renders and extracts correctly.
  - Publish job end to end: maps created, shared, analytics row logged, links shown.
  - Refreshed session is saved back to the browser.
  - Expired/invalid session produces `SESSION_EXPIRED` message.

## Deployment

- Vercel project on this repo, branch `feature/cloud-hosting` (preview) → `main` once merged.
- The WASM bundle is built in GitHub Actions (`.github/workflows/web.yml`, `wasm-tools`
  workload), not on Vercel — Vercel's build image has no emscripten/workload support. The
  workflow uploads the static `wwwroot` with `vercel deploy` (main → production, branches →
  preview). Phase 4 adds the `api/` Node functions and the daily blob-cleanup cron to the
  same deploy.
- Private worker repo created by the user; holds `.github/workflows/publish.yml` and secrets.
- `release.yml` (desktop releases on `v*` tags) is untouched.

## Build order

Each phase leaves the app working.

1. Split `Core` → `Core` + `Core.Publish`; extract shared UI into `GmapPlanner.UI` (desktop host stays `GmapPlanner.App`); in-memory KML
   from `PipelineService`. Desktop behaviour unchanged, tests green.
2. `App.Browser`: WASM host, browser settings storage, client-side generation, KML downloads,
   PDF CORS check. Deploy static site to Vercel. Deployed by GitHub Actions + Vercel CLI (see Deployment).
3. `/api/usage` and `/api/analytics`.
4. `LoginHelper`, `MyMapsSession` storage-state mode, `Worker`, jobs API, worker workflow.
5. Manual end-to-end checklist.

## Rollback

All work is on `feature/cloud-hosting`. Undo by not merging or deleting the branch; the Vercel
project and the private worker repo can be deleted independently.

## Out of scope

Site accounts/login, server-side key storage, remote in-browser Google login, Docker hosting
(fallback only), in-app updater in the browser build.
