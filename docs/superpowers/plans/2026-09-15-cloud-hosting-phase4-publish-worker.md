# Cloud Hosting Phase 4 — Login Helper, Cloud Publish Worker, Jobs API

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the browser host publish itineraries to Google My Maps (and share via Drive, log to the Analytics Sheet) by dispatching a per-job GitHub Actions run in a private worker repo that reuses the existing C# publish code, driven by a saved Google session the user produces with a local login helper.

**Architecture:** The browser POSTs an encrypted job (KMLs + recipients + the user's `session.json` + optional SA JSON/Sheet id) to `POST /api/jobs`. Vercel encrypts it into Blob storage and dispatches a `workflow_dispatch` in the private worker repo with only the job id. The worker (`GmapPlanner.Worker`, a .NET console app referencing `Core.Publish`) claims the job over HMAC-authenticated calls, decrypts it, runs `MyMapsSession` in a new **storage-state** mode against the runner's Chrome, shares via `DriveShareService`, logs via `SheetsAnalyticsService`, and posts status back. The browser polls `GET /api/jobs/:id`, shows map links, and saves the refreshed session back to localStorage.

**Tech Stack:** C#/.NET 8 (`GmapPlanner.LoginHelper` win-x64/osx-arm64 console, `GmapPlanner.Worker` console, both referencing `Core.Publish`), Playwright, Google.Apis (Drive/Sheets). Vercel TypeScript functions (Node 20, `crypto` AES-256-GCM + HMAC-SHA256, `@vercel/blob`). GitHub Actions `workflow_dispatch`.

## Global Constraints

- **No new site auth.** Job authorization is: possession of the site URL to *submit*, and the shared `WORKER_HMAC` to *claim/report*. Abuse mitigations (rate limit, size caps) are mandatory (see Task 7).
- **Known risk — session replay (spec §Known risks + CLAUDE.md):** runners use datacenter IPs; Google may reject a replayed session. This is surfaced as `SESSION_EXPIRED`, never a crash, and the UI tells the user to re-run the login helper. This plan does **not** try to defeat the re-challenge; it makes failure clean and recoverable.
- **Secrets:** the `session.json`, SA JSON, and all keys live only in the browser and in encrypted job blobs. Vercel env holds `JOB_KEY` (32-byte hex, AES-256-GCM), `WORKER_HMAC`, `GITHUB_TOKEN` (fine-grained, Actions:write on the worker repo only), `WORKER_REPO`, `BLOB_READ_WRITE_TOKEN`. Worker repo secrets: `WORKER_HMAC`, `API_BASE_URL`. Blob store holds ciphertext only.
- **Desktop behaviour must not change.** `MyMapsSession`'s existing persistent-profile launch, `PublishService`, `DriveShareService` desktop paths stay; storage-state mode is *additive*.
- **Trimming rule #1 (CLAUDE.md)** applies to all C#: no reflection-based `System.Text.Json`; source-gen contexts only. `session.json` / job-payload DTOs go through a source-gen context (Core.Publish already has `PublishJsonContext`).
- **Vercel functions:** TypeScript, Node 20, dependency budget = `@vercel/blob` only; crypto is Node built-in.
- **Encryption:** AES-256-GCM, random 12-byte IV per payload, auth tag appended; `JOB_KEY` is 32 bytes. HMAC-SHA256 over the raw request body with `WORKER_HMAC`, constant-time compare.

---

## File Structure

```
src/GmapPlanner.LoginHelper/           # NEW — console exe, produces session.json
  Program.cs
  GmapPlanner.LoginHelper.csproj
src/GmapPlanner.Worker/                # NEW — console app, runs one publish job on a runner
  Program.cs
  WorkerApi.cs                         # claim / status calls (HMAC)
  GmapPlanner.Worker.csproj
src/GmapPlanner.Core.Publish/
  Services/Publish/MyMapsSession.cs    # MODIFY: add storage-state launch mode + refreshed-state export + SESSION_EXPIRED
  Models/SessionFile.cs                # NEW: session.json shape (version, storageState, driveCredentials, driveToken)
  Models/JobPayload.cs                 # NEW: decrypted job shape
  Json/PublishJsonContext.cs           # MODIFY: add SessionFile, JobPayload, status DTOs
web/api/
  jobs/index.ts                        # POST /api/jobs  (submit)
  jobs/[id]/claim.ts                   # POST /api/jobs/:id/claim  (worker, HMAC)
  jobs/[id]/status.ts                  # POST /api/jobs/:id/status (worker, HMAC)
  jobs/[id]/index.ts                   # GET  /api/jobs/:id        (browser poll)
  cron/cleanup.ts                      # daily blob GC
  _lib/crypto.ts                       # AES-256-GCM + HMAC + timing-safe compare
  _lib/blob.ts                         # Blob get/put/del wrappers
  _lib/dispatch.ts                     # GitHub workflow_dispatch
web/api/_lib/__tests__/crypto.test.ts  # NEW — vitest: AES round-trip, HMAC accept/reject
web/vercel.json                        # MODIFY: add crons; functions already declared (phase 3)
src/GmapPlanner.App.Browser/
  Platform/BrowserPlatformServices.cs  # MODIFY: PublishAsync → POST /api/jobs + poll; session.json load/save
  BrowserApi.cs                        # MODIFY: add SubmitJobAsync / PollJobAsync
src/GmapPlanner.UI/
  Views/MainView.axaml(.cs)            # MODIFY: session.json drop zone (browser-only, Features.Publish)
  ViewModels/MainViewModel.cs          # MODIFY: LoadSessionAsync; publish enabled only when a session is loaded
  Platform/IPlatformServices.cs        # MODIFY: session load/save on the seam
docs/worker-repo/                      # NEW — template the user copies into their private repo
  publish.yml
  README.md
tests/GmapPlanner.Core.Tests/          # session.json + job payload parsing tests
```

---

## Global data contracts (used across tasks)

`session.json` (produced by LoginHelper, consumed by Worker):
```json
{ "version": 1, "storageState": { }, "driveCredentials": { }, "driveToken": { } }
```

Job submit body (browser → `POST /api/jobs`):
```json
{ "tripName": "…", "kmls": [{ "name": "day1.kml", "content": "<kml/>" }],
  "recipients": ["a@x.com"], "role": "reader", "notify": true,
  "session": { }, "saJson": { }, "sheetId": "…" }
```

Status (worker → `POST /api/jobs/:id/status`, browser reads via `GET /api/jobs/:id`):
```json
{ "state": "queued|running|done|failed", "message": "map 2/3",
  "maps": [{ "title": "…", "url": "…" }], "error": "…", "errorCode": "SESSION_EXPIRED|…",
  "refreshedSession": { } }
```

---

### Task 1: `SessionFile` / `JobPayload` models + `PublishJsonContext` + parsing tests

**Files:**
- Create: `src/GmapPlanner.Core.Publish/Models/SessionFile.cs`, `Models/JobPayload.cs`, `Models/JobStatus.cs`
- Modify: `src/GmapPlanner.Core.Publish/Json/PublishJsonContext.cs`
- Test: `tests/GmapPlanner.Core.Tests/SessionAndJobParsingTests.cs`

**Interfaces:**
- Produces: `SessionFile(int Version, JsonElement StorageState, JsonElement DriveCredentials, JsonElement DriveToken)`, `JobPayload(string TripName, IReadOnlyList<KmlFile> Kmls, IReadOnlyList<string> Recipients, string Role, bool Notify, SessionFile Session, JsonElement? SaJson, string? SheetId)`, `JobStatus(...)`. `KmlFile` is the existing `Core` record.

- [ ] **Step 1: Failing test**
```csharp
[Fact]
public void SessionFile_parses_v1()
{
    var json = """{"version":1,"storageState":{"cookies":[]},"driveCredentials":{"installed":{}},"driveToken":{"access_token":"x"}}""";
    var s = JsonSerializer.Deserialize(json, PublishJsonContext.Default.SessionFile);
    Assert.Equal(1, s!.Version);
    Assert.True(s.StorageState.TryGetProperty("cookies", out _));
}
```
- [ ] **Step 2:** Run → fails (types missing).
- [ ] **Step 3:** Add the records (use `JsonElement` for the opaque Google blobs so we never reflect over Google's shapes) and register all three in `PublishJsonContext` with `[JsonSerializable]`.
- [ ] **Step 4:** Run → passes.
- [ ] **Step 5:** Commit `feat: session.json / job payload models + source-gen serialization`.

---

### Task 2: `MyMapsSession` storage-state launch mode

**Files:**
- Modify: `src/GmapPlanner.Core.Publish/Services/Publish/MyMapsSession.cs`
- Test: `tests/GmapPlanner.Core.Tests` — extend the existing `MyMapsImport`/session tests only for the parts that don't need a browser (the launch-mode selection and the `SESSION_EXPIRED` detection predicate).

**Interfaces:**
- Consumes: `SessionFile.StorageState`.
- Produces: `MyMapsSession.LaunchFromStorageStateAsync(JsonElement storageState, bool headless = true)` creating a fresh Playwright context from the storage state (not the persistent profile); `Task<JsonElement> ExportStorageStateAsync()` returning the refreshed state after a run; an `OnGoogleSignInRedirect` signal that the publish loop turns into a `PublishException` with `ErrorCode = "SESSION_EXPIRED"`.

- [ ] **Step 1:** Read `MyMapsSession.cs` fully. Identify the persistent-profile launch (`LaunchPersistentAsync`) and where the editor is opened. The storage-state mode is a sibling: `browser = await playwright.Chromium.LaunchAsync(new(){ Headless = headless, Channel = "chrome" }); context = await browser.NewContextAsync(new(){ StorageStateContent = storageStateJson })`.
- [ ] **Step 2:** Add `SESSION_EXPIRED` detection: after opening the My Maps editor, if the URL redirects to `accounts.google.com/…signin` or the editor never reaches an authenticated state within the existing timeout, throw `PublishException` with `ErrorCode = "SESSION_EXPIRED"`. (Add `ErrorCode` to `PublishException` if absent.)
- [ ] **Step 3:** Add `ExportStorageStateAsync()` → `context.StorageStateAsync()` parsed to `JsonElement`, called by the worker after a successful (or partial) run so cookies rotate.
- [ ] **Step 4:** Unit-test the pure bits: the redirect-URL predicate (`IsSignInRedirect(string url)`) and that `LaunchFromStorageStateAsync` picks the non-persistent path. Browser-driven behaviour stays in the manual E2E.
- [ ] **Step 5:** Build `Core.Publish` + desktop `App` (unchanged behaviour), run tests. Commit `feat: MyMapsSession storage-state launch + SESSION_EXPIRED`.

> Reviewer note: the persistent-profile desktop path and all the hard-won Picker guards in CLAUDE.md must be **untouched**. Storage-state mode reuses the same import/rename/verify logic; only the launch and the auth-failure detection are new.

---

### Task 3: `GmapPlanner.LoginHelper`

**Files:**
- Create: `src/GmapPlanner.LoginHelper/Program.cs`, `.csproj`
- (No new tests — this is an interactive tool; its output shape is covered by Task 1's `SessionFile` parsing test.)

**Interfaces:**
- Consumes: `MyMapsSession.LoginAsync` (existing headed login), `DriveShareService` OAuth consent.
- Produces: writes `session.json` (Task 1 shape) to the working dir; prints the path.

- [ ] **Step 1:** New console csproj (win-x64 + osx-arm64, references `Core.Publish`, sets `PlaywrightPlatform` per RID like `App.csproj`, `PublishSingleFile`).
- [ ] **Step 2:** `Program.cs`: run the existing headed `MyMapsSession.LoginAsync` (persistent profile, real Chrome/Edge) so the user signs in once; export the storage state. Then run `DriveShareService` OAuth (user picks their `credentials.json`) to get the Drive token. Assemble `SessionFile` and write `session.json` via `PublishJsonContext`.
- [ ] **Step 3:** Errors: if login falls back to bundled Chromium (`OnBundledChromium`), print the existing "install Chrome/Edge" message and exit non-zero (mirror `LoginAsync` desktop behaviour).
- [ ] **Step 4:** Build + publish both RIDs; smoke-run on the dev machine (produces a parseable `session.json`). Commit `feat: GmapPlanner.LoginHelper produces session.json`.

> The LoginHelper is shipped like the desktop app: add it to `release.yml` as an extra artifact per OS (small addition; keep the existing installer/dmg jobs untouched). If that expands scope, ship it as a plain zipped single-file exe attached to the same release.

---

### Task 4: `web/api/_lib/crypto.ts` (AES-256-GCM + HMAC) — TDD

**Files:**
- Create: `web/api/_lib/crypto.ts`, `web/api/_lib/blob.ts`, `web/api/_lib/dispatch.ts`
- Test: `web/api/_lib/__tests__/crypto.test.ts`

**Interfaces:**
- Produces: `encrypt(plaintext: string, keyHex: string): string` (returns `iv.tag.ciphertext` base64url, dot-joined), `decrypt(token: string, keyHex: string): string`, `hmac(body: string, secret: string): string`, `verifyHmac(body, secret, provided): boolean` (timing-safe).

- [ ] **Step 1: Failing tests**
```ts
import { describe, it, expect } from "vitest";
import { encrypt, decrypt, hmac, verifyHmac } from "../crypto.js";
const KEY = "00".repeat(32); // 32-byte hex

describe("crypto", () => {
  it("AES-256-GCM round-trips", () => {
    const pt = JSON.stringify({ hello: "world", n: 5 });
    const ct = encrypt(pt, KEY);
    expect(ct).not.toContain("world");
    expect(decrypt(ct, KEY)).toBe(pt);
  });
  it("decrypt rejects a tampered tag", () => {
    const ct = encrypt("secret", KEY).split(".");
    ct[1] = Buffer.from("tampered").toString("base64url");
    expect(() => decrypt(ct.join("."), KEY)).toThrow();
  });
  it("HMAC accepts a matching signature and rejects a wrong one", () => {
    const sig = hmac("body", "s3cret");
    expect(verifyHmac("body", "s3cret", sig)).toBe(true);
    expect(verifyHmac("body", "s3cret", hmac("body", "other"))).toBe(false);
  });
});
```
- [ ] **Step 2:** Run → fails.
- [ ] **Step 3: Implement** using `node:crypto` (`createCipheriv("aes-256-gcm", key, iv)`, random 12-byte iv, `cipher.getAuthTag()`; `timingSafeEqual` for HMAC). `blob.ts` wraps `@vercel/blob` `put/head/del` (or the REST API with `BLOB_READ_WRITE_TOKEN`); `dispatch.ts` calls the GitHub `workflow_dispatch` REST endpoint with `GITHUB_TOKEN`, `WORKER_REPO`, ref `main`, input `{ job_id }`.
- [ ] **Step 4:** Run → passes.
- [ ] **Step 5:** Commit `web: AES-256-GCM + HMAC + blob/dispatch helpers`.

---

### Task 5: Jobs API endpoints

**Files:**
- Create: `web/api/jobs/index.ts`, `jobs/[id]/claim.ts`, `jobs/[id]/status.ts`, `jobs/[id]/index.ts`
- (Endpoint logic is thin over Task 4's tested helpers; the crypto/HMAC tests cover the security-critical parts.)

**Interfaces (contracts):**
- `POST /api/jobs` `{submit body}` → validate size (≤2MB) + ≤10 KMLs; `id = randomUUID()`; `encrypt(JSON.stringify(payload), JOB_KEY)` → Blob `jobs/<id>`; Blob `status/<id> = {state:"queued"}`; `dispatch({job_id:id})`; return `{ id }`. On dispatch failure: set status `failed`, return 502.
- `POST /api/jobs/:id/claim` — `verifyHmac(rawBody, WORKER_HMAC, header "x-worker-signature")`; read+`decrypt` `jobs/<id>`; **delete** `jobs/<id>` (single claim); return the decrypted payload. 401 on bad HMAC, 404 if already claimed.
- `POST /api/jobs/:id/status` — HMAC; body is a status object; if it contains `refreshedSession`, `encrypt` it into the stored status blob; write `status/<id>`. 401/404 as above.
- `GET /api/jobs/:id` — browser poll (no HMAC; the id is the capability). Return the status; if `state` is `done`/`failed`, `decrypt` `refreshedSession` for the response and **delete** `status/<id>` after this final read.

- [ ] **Step 1:** Implement the four handlers using `crypto.ts`, `blob.ts`, `dispatch.ts`, and phase-3 `http.ts` (CORS + `preflight` on `POST /api/jobs` and `GET /api/jobs/:id`; the worker endpoints need no CORS).
- [ ] **Step 2:** Enforce the abuse caps from the spec in `POST /api/jobs`: reject >2MB bodies and >10 KMLs with 413; document the Vercel firewall rate-limit rule (10/hour/IP on `/api/jobs`) in the deploy doc (Task 8).
- [ ] **Step 3:** `npx tsc --noEmit` clean. Commit `web: jobs API (submit/claim/status/poll) with encrypted blobs`.

---

### Task 6: `GmapPlanner.Worker`

**Files:**
- Create: `src/GmapPlanner.Worker/Program.cs`, `WorkerApi.cs`, `.csproj`

**Interfaces:**
- Consumes: `POST /api/jobs/:id/claim`, `POST /api/jobs/:id/status`; `MyMapsSession` storage-state mode, `DriveShareService`, `SheetsAnalyticsService.RecordPublishAsync`.
- Produces: status callbacks; exit code reflects success/failure.

- [ ] **Step 1:** New console csproj referencing `Core.Publish`, `PlaywrightPlatform` = host RID (Linux runner), reads env `JOB_ID`, `API_BASE_URL`, `WORKER_HMAC`.
- [ ] **Step 2:** `WorkerApi.cs`: `ClaimAsync()` and `PostStatusAsync(JobStatus)` — compute `x-worker-signature = hmac(body, WORKER_HMAC)` matching `crypto.ts` (hex, over the exact serialized body). Serialize/deserialize via `PublishJsonContext`.
- [ ] **Step 3:** `Program.cs`: claim → write KMLs + `credentials.json` + Drive token to a temp dir → `MyMapsSession.LaunchFromStorageStateAsync(session.storageState)` → publish loop (posting `{state:"running", message:"map i/n"}`) → `DriveShareService` share → `SheetsAnalyticsService.RecordPublishAsync` if `saJson`+`sheetId` present → `ExportStorageStateAsync()` → post `{state:"done", maps, refreshedSession}`. On `PublishException{ErrorCode:"SESSION_EXPIRED"}` post `{state:"failed", errorCode:"SESSION_EXPIRED"}`; on any other exception post `{state:"failed", error}` including maps created before the failure. Always delete the temp dir.
- [ ] **Step 4:** Build; unit-test `WorkerApi` HMAC signing against a known vector that matches the `crypto.test.ts` expectation (shared secret + body → same hex). Commit `feat: GmapPlanner.Worker runs a publish job on a runner`.

---

### Task 7: Browser publish wiring + session.json drop zone

**Files:**
- Modify: `BrowserApi.cs` (`SubmitJobAsync`, `PollJobAsync`), `BrowserPlatformServices.cs` (`PublishAsync`, session load/save), `MainView.axaml(.cs)` (session drop zone, `Features.Publish`), `MainViewModel.cs` (`LoadSessionAsync`, publish enabled only when a session is loaded), `IPlatformServices.cs`.
- Add job DTOs to `GmapPlannerJsonContext`/`PublishJsonContext` as needed (source-gen).

**Interfaces:**
- Consumes: jobs API.
- Produces: `BrowserPlatformServices.PublishAsync(tripName, files, recipients, role, notify, showBrowser, progress)` submits a job and polls, mapping `{state:"running", message}` to the existing `ProgressCallback`, returning the `maps` links; saves `refreshedSession` to localStorage; sets `SESSION_EXPIRED` into the error banner text per spec.

- [ ] **Step 1:** Flip `BrowserPlatformServices.Features.Publish = true`. Add a `session.json` file drop zone + "Load session" to Settings, gated `Features.Publish`, storing the session in localStorage (`gmapplanner.session`). Publish button disabled with the spec's hint when no session is loaded.
- [ ] **Step 2:** `PublishAsync`: `POST /api/jobs` with the KMLs, recipients, role, notify, the stored session, and (if present) SA JSON + Sheet id; then poll `GET /api/jobs/:id` every 5s, forwarding `message` to `progress`. On `done`: return maps; save `refreshedSession`. On `failed`: throw with `error`/`errorCode` → banner. Treat 10 min without a status change as failed (spec).
- [ ] **Step 3:** Build both hosts; run tests. Commit `Browser: cloud publish via jobs API + session.json`.

---

### Task 8: Worker workflow template, cron cleanup, secrets doc

**Files:**
- Create: `docs/worker-repo/publish.yml`, `docs/worker-repo/README.md`
- Create: `web/api/cron/cleanup.ts`; modify `web/vercel.json` (add `crons`)
- Modify: `CLAUDE.md` (a "Cloud publish (phase 4)" section)

- [ ] **Step 1:** `docs/worker-repo/publish.yml`: `workflow_dispatch` input `job_id`; `runs-on: ubuntu-latest`; `timeout-minutes: 20`; checkout **this** repo at `main`; setup .NET 8; `dotnet run --project src/GmapPlanner.Worker` with env `JOB_ID`, `API_BASE_URL` (secret), `WORKER_HMAC` (secret); `if: failure()` step posts a `failed` status via curl + an HMAC computed in bash (`openssl dgst -sha256 -hmac`).
- [ ] **Step 2:** `web/api/cron/cleanup.ts`: delete `jobs/*` and `status/*` blobs older than 1h. Add to `web/vercel.json`: `"crons": [{ "path": "/api/cron/cleanup", "schedule": "0 3 * * *" }]`.
- [ ] **Step 3:** `docs/worker-repo/README.md` + a CLAUDE.md section: the private-repo setup, the exact secrets table (spec §Secrets), the Vercel firewall rate-limit rule on `/api/jobs`, and the Cloud Run Jobs fallback note (spec §Known risks) if Actions is flagged.
- [ ] **Step 4:** Commit `web+docs: cron cleanup, worker workflow template, phase-4 secrets doc`.

---

### Task 9: Manual end-to-end checklist (controller + user)

Not an implementer task — run after Task 8, on the deployed preview + a real private worker repo.

- [ ] Run `GmapPlanner.LoginHelper` → produces `session.json`; drop it into web Settings.
- [ ] Publish a small trip: job dispatched → Actions run visible → maps created in My Maps → shared to a recipient → links shown in the browser.
- [ ] Analytics row logged (if SA JSON + Sheet id set).
- [ ] Refreshed session saved back (a second publish works without re-login).
- [ ] Expired session (revoke/clear cookies) → `SESSION_EXPIRED` banner with the re-login hint; no crash.
- [ ] Abuse caps: a >2MB job and an 11-KML job are rejected client-side and server-side.

---

## Self-Review

- **Spec coverage:** LoginHelper (§Components) ✅; MyMapsSession storage-state + SESSION_EXPIRED (§Components) ✅; Worker (§Components) ✅; jobs API submit/claim/status/poll + encryption + HMAC (§Architecture, §Data flow) ✅; cron cleanup (§Architecture) ✅; worker workflow (§Worker workflow) ✅; secrets table (§Secrets) ✅; abuse mitigations + Cloud Run fallback (§Error handling, §Known risks) ✅; refreshed-session save-back (§Data flow) ✅.
- **Placeholders:** none intended. The "reviewer notes" (Tasks 2, 3) are guardrails, not gaps: the desktop persistent-profile path and Picker guards must stay untouched, and the exact HMAC encoding must match `crypto.ts` (Task 6 pins it with a shared test vector).
- **Type consistency:** `KmlFile` (Core), `PublishException.ErrorCode` (Task 2 adds it), `JobStatus`/`SessionFile`/`JobPayload` (Task 1) are used consistently by Worker (Task 6) and browser (Task 7). The HMAC contract is defined once in Task 4 and matched in Task 6 with a shared vector.
- **Risk called out:** the session-replay re-challenge is documented in Global Constraints and handled as `SESSION_EXPIRED` end-to-end; this plan makes failure clean, it does not claim to prevent Google's re-challenge.
- **Decomposition:** phase 4 depends on phase 3 only for the shared `web/api/_lib/http.ts` + the deploy shape; it is otherwise independent and can be built after phase 3 ships.
