# Cloud Hosting Phase 3 — Usage Ring + Analytics via Vercel API

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restore the live geocoding-usage ring and the Analytics-Sheet page in the browser host by adding two stateless Vercel serverless functions that do the Google-authenticated calls the browser cannot make cross-origin.

**Architecture:** The service-account JSON and Sheet id live in browser localStorage (as the design's secret-storage model already allows). The browser POSTs the SA JSON to `POST /api/usage` (Cloud Monitoring) and `POST /api/analytics` (Sheets read); each function signs the SA JWT with Node `crypto`, calls Google, and returns JSON. The functions hold **no** persistent secrets — the SA JSON is per-request, used and discarded. Desktop behaviour is untouched: `DesktopPlatformServices` still calls `UsageService`/`SheetsAnalyticsService` directly.

**Tech Stack:** Vercel serverless functions (TypeScript, Node 20 runtime, no framework — Web `Request`/`Response`), Google service-account JWT (RS256) via Node `crypto`, Cloud Monitoring `timeSeries` REST, Sheets `values` REST. Browser side: C#/.NET 8 `net8.0-browser`, `HttpClient` via `[JSImport]`-free `System.Net.Http` (Avalonia.Browser supports `HttpClient`), Avalonia 11.2.5.

## Global Constraints

- **No new site auth.** The SA JSON travels in the request body over HTTPS; functions store nothing.
- **Desktop behaviour must not change.** Only `BrowserPlatformServices` and the shared VM gating change; `DesktopPlatformServices`, `UsageService`, `SheetsAnalyticsService` stay as they are.
- **Trimming rule #1 (CLAUDE.md):** never call reflection-based `System.Text.Json` in C#; add DTOs to `Core/Json/GmapPlannerJsonContext.cs` and pass `JsonTypeInfo`. The WASM publish must emit no new `IL2026` from our own code.
- **Free-cap constant:** `GeoMonthlyLimit = 5000` (Places API Text Search Pro free cap) — reuse `AppConfig.GeoMonthlyLimit`, do not hardcode.
- **Vercel functions are TypeScript, Node 20, zero-dependency** (Node built-ins only: `crypto`, `fetch`). No `googleapis` SDK.
- **CORS:** the functions must return `Access-Control-Allow-Origin` for the site origin and handle `OPTIONS` preflight, because the WASM app fetches them from the same Vercel deployment (same origin in production, but preflight still applies for non-simple POST with JSON).
- **The SA JSON is a secret in transit** — never log it, never put it in a URL/query string, only in the POST body.

---

## File Structure

```
web/                                  # NEW — the Vercel deploy root (static site + functions)
  api/
    usage.ts                          # POST: SA JSON → Cloud Monitoring → gauge
    analytics.ts                      # POST: SA JSON + sheetId → Sheets read → rows
    _lib/
      googleAuth.ts                   # SA JWT sign (RS256) + token exchange
      http.ts                         # CORS headers, JSON helpers, method guard
  vercel.json                         # functions runtime + static routing
  package.json                        # type: module, node engine
  tsconfig.json
web/api/_lib/__tests__/               # NEW — vitest
  googleAuth.test.ts
src/GmapPlanner.App.Browser/
  Platform/BrowserPlatformServices.cs # MODIFY: GetUsageAsync / FetchAnalyticsAsync call the API
  BrowserApi.cs                       # NEW: typed fetch client for /api/usage, /api/analytics
src/GmapPlanner.UI/
  ViewModels/MainViewModel.cs         # MODIFY: revert the phase-2 SA/Sheet gating; usage/analytics visible when the host supports it
  Platform/IPlatformServices.cs       # MODIFY: PlatformFeatures gains ApiBaseUrl? (or Analytics stays the gate) — see Task 4
.github/workflows/web.yml             # MODIFY: deploy the `web/` root (static + functions), not just wwwroot
CLAUDE.md                             # MODIFY: document the api/ functions and the new deploy shape
docs/superpowers/specs/2026-09-15-cloud-hosting-design.md  # no change (already specced)
```

The functions are grouped by responsibility: `_lib/googleAuth.ts` is the only place that knows how to turn an SA JSON into a Google access token; `usage.ts` and `analytics.ts` are thin endpoint wrappers. This keeps the JWT/crypto logic in one tested unit.

---

## Interfaces (produced by this plan, consumed by phase 4)

- `web/api/_lib/googleAuth.ts` exports `getAccessToken(saJson: object, scope: string): Promise<string>` and `signJwt(saJson, scope): string`. Phase 4's `/api/analytics` write path (if added) reuses these.
- `web/api/_lib/http.ts` exports `cors(origin?: string): Headers`, `readJson<T>(req: Request): Promise<T>`, `json(body, status?, extraHeaders?): Response`, `preflight(req): Response | null`.
- `BrowserApi` (C#) exposes `Task<UsageGauge?> GetUsageAsync(string saJson, CancellationToken)` and `Task<IReadOnlyList<AnalyticsRow>?> FetchAnalyticsAsync(string saJson, string sheetId, CancellationToken)` — same return types the desktop path already returns, so the VM is unchanged below the `IPlatformServices` seam.

---

### Task 1: Google auth helper (`_lib/googleAuth.ts`) + http helper

**Files:**
- Create: `web/package.json`, `web/tsconfig.json`, `web/vercel.json`
- Create: `web/api/_lib/http.ts`
- Create: `web/api/_lib/googleAuth.ts`
- Test: `web/api/_lib/__tests__/googleAuth.test.ts`

**Interfaces:**
- Produces: `signJwt(saJson, scope)`, `getAccessToken(saJson, scope)`, `cors`, `readJson`, `json`, `preflight`.

- [ ] **Step 1: Scaffold `web/`**

`web/package.json`:
```json
{
  "name": "gmap-planner-web",
  "private": true,
  "type": "module",
  "engines": { "node": "20.x" },
  "scripts": { "test": "vitest run" },
  "devDependencies": { "typescript": "^5.6.0", "vitest": "^2.1.0", "@types/node": "^20.16.0" }
}
```

`web/tsconfig.json`:
```json
{
  "compilerOptions": {
    "target": "ES2022",
    "module": "ES2022",
    "moduleResolution": "Bundler",
    "strict": true,
    "esModuleInterop": true,
    "skipLibCheck": true,
    "types": ["node"]
  }
}
```

`web/vercel.json` (functions use the Node runtime; everything else is served static from `public/`):
```json
{
  "$schema": "https://openapi.vercel.sh/vercel.json",
  "functions": { "api/*.ts": { "runtime": "@vercel/node@5" } }
}
```

- [ ] **Step 2: Write the failing test for `signJwt`**

`web/api/_lib/__tests__/googleAuth.test.ts`:
```ts
import { describe, it, expect } from "vitest";
import { generateKeyPairSync } from "node:crypto";
import { signJwt } from "../googleAuth.js";

function testSaJson() {
  const { privateKey } = generateKeyPairSync("rsa", { modulusLength: 2048 });
  const pem = privateKey.export({ type: "pkcs8", format: "pem" }).toString();
  return { client_email: "svc@proj.iam.gserviceaccount.com", private_key: pem, token_uri: "https://oauth2.googleapis.com/token" };
}

describe("signJwt", () => {
  it("produces a three-part JWT with the requested scope and issuer", () => {
    const sa = testSaJson();
    const jwt = signJwt(sa, "https://www.googleapis.com/auth/monitoring.read");
    const [h, p] = jwt.split(".");
    expect(jwt.split(".")).toHaveLength(3);
    const header = JSON.parse(Buffer.from(h, "base64url").toString());
    const claims = JSON.parse(Buffer.from(p, "base64url").toString());
    expect(header).toMatchObject({ alg: "RS256", typ: "JWT" });
    expect(claims.iss).toBe(sa.client_email);
    expect(claims.scope).toBe("https://www.googleapis.com/auth/monitoring.read");
    expect(claims.aud).toBe(sa.token_uri);
    expect(claims.exp - claims.iat).toBe(3600);
  });
});
```

- [ ] **Step 3: Run the test to verify it fails**

Run: `cd web && npm install && npm test`
Expected: FAIL — `../googleAuth.js` not found.

- [ ] **Step 4: Implement `http.ts`**

`web/api/_lib/http.ts`:
```ts
export function cors(origin = "*"): Record<string, string> {
  return {
    "Access-Control-Allow-Origin": origin,
    "Access-Control-Allow-Methods": "POST, OPTIONS",
    "Access-Control-Allow-Headers": "content-type",
    "Access-Control-Max-Age": "86400",
  };
}

export function preflight(req: Request): Response | null {
  if (req.method === "OPTIONS") return new Response(null, { status: 204, headers: cors() });
  return null;
}

export function json(body: unknown, status = 200, extra: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json", ...cors(), ...extra },
  });
}

export async function readJson<T>(req: Request): Promise<T> {
  return (await req.json()) as T;
}
```

- [ ] **Step 5: Implement `googleAuth.ts`**

`web/api/_lib/googleAuth.ts`:
```ts
import { createSign } from "node:crypto";

interface SaJson { client_email: string; private_key: string; token_uri?: string; }

function b64url(input: Buffer | string): string {
  return Buffer.from(input).toString("base64url");
}

export function signJwt(sa: SaJson, scope: string): string {
  const iat = Math.floor(Date.now() / 1000);
  const aud = sa.token_uri ?? "https://oauth2.googleapis.com/token";
  const header = b64url(JSON.stringify({ alg: "RS256", typ: "JWT" }));
  const claims = b64url(JSON.stringify({ iss: sa.client_email, scope, aud, iat, exp: iat + 3600 }));
  const signingInput = `${header}.${claims}`;
  const signature = createSign("RSA-SHA256").update(signingInput).sign(sa.private_key, "base64url");
  return `${signingInput}.${signature}`;
}

export async function getAccessToken(sa: SaJson, scope: string): Promise<string> {
  const assertion = signJwt(sa, scope);
  const res = await fetch(sa.token_uri ?? "https://oauth2.googleapis.com/token", {
    method: "POST",
    headers: { "content-type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({ grant_type: "urn:ietf:params:oauth:grant-type:jwt-bearer", assertion }),
  });
  if (!res.ok) throw new Error(`token exchange failed: ${res.status}`);
  const data = (await res.json()) as { access_token?: string };
  if (!data.access_token) throw new Error("token exchange returned no access_token");
  return data.access_token;
}
```

- [ ] **Step 6: Run the test to verify it passes**

Run: `cd web && npm test`
Expected: PASS.

- [ ] **Step 7: Commit**

```bash
git add web/package.json web/tsconfig.json web/vercel.json web/api/_lib/ 
git commit -m "web: Google SA JWT auth + http helpers for the Vercel API"
```

---

### Task 2: `POST /api/usage`

**Files:**
- Create: `web/api/usage.ts`
- Test: extend `web/api/_lib/__tests__/googleAuth.test.ts` is enough for signing; `usage.ts` itself is thin (its Google calls are integration-only, verified in the manual checklist).

**Interfaces:**
- Consumes: `getAccessToken`, `cors`, `json`, `preflight`, `readJson`.
- Produces: `POST /api/usage` `{ saJson: object }` → `200 { used, limit, percent, resetDays }` or `{ error }` (best-effort; the browser hides the ring on any non-200/`null`).

- [ ] **Step 1: Implement `usage.ts`**

Port `UsageService` (Cloud Monitoring `timeSeries` for `serviceruntime.googleapis.com/api/request_count` filtered to `places.googleapis.com`, month-to-date sum) to TypeScript. `web/api/usage.ts`:
```ts
import { getAccessToken } from "./_lib/googleAuth.js";
import { readJson, json, preflight } from "./_lib/http.js";

const LIMIT = 5000; // AppConfig.GeoMonthlyLimit — Places Text Search Pro free cap
const SCOPE = "https://www.googleapis.com/auth/monitoring.read";

export default async function handler(req: Request): Promise<Response> {
  const pre = preflight(req);
  if (pre) return pre;
  if (req.method !== "POST") return json({ error: "POST only" }, 405);

  try {
    const { saJson } = await readJson<{ saJson: any }>(req);
    if (!saJson?.client_email || !saJson?.project_id) return json({ error: "bad sa json" }, 400);

    const token = await getAccessToken(saJson, SCOPE);
    const now = new Date();
    const start = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), 1)).toISOString();
    const end = now.toISOString();
    const filter =
      'metric.type="serviceruntime.googleapis.com/api/request_count" ' +
      'resource.type="consumed_api" ' +
      'resource.label."service"="places.googleapis.com"';
    const url =
      `https://monitoring.googleapis.com/v3/projects/${saJson.project_id}/timeSeries` +
      `?filter=${encodeURIComponent(filter)}` +
      `&interval.startTime=${encodeURIComponent(start)}` +
      `&interval.endTime=${encodeURIComponent(end)}` +
      `&aggregation.alignmentPeriod=2592000s` +
      `&aggregation.perSeriesAligner=ALIGN_SUM` +
      `&aggregation.crossSeriesReducer=REDUCE_SUM`;

    const res = await fetch(url, { headers: { authorization: `Bearer ${token}` } });
    if (!res.ok) return json({ error: `monitoring ${res.status}` }, 200); // best-effort → ring hidden
    const data = (await res.json()) as any;
    let used = 0;
    for (const series of data.timeSeries ?? [])
      for (const pt of series.points ?? []) used += Number(pt.value?.int64Value ?? pt.value?.doubleValue ?? 0);

    const daysInMonth = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth() + 1, 0)).getUTCDate();
    const resetDays = daysInMonth - now.getUTCDate();
    const percent = Math.min(100, Math.round((used / LIMIT) * 100));
    return json({ used, limit: LIMIT, percent, resetDays });
  } catch (e) {
    return json({ error: String((e as Error).message ?? e) }, 200); // hide the ring, never 500 the UI
  }
}
```

- [ ] **Step 2: Type-check**

Run: `cd web && npx tsc --noEmit`
Expected: no errors.

- [ ] **Step 3: Commit**

```bash
git add web/api/usage.ts
git commit -m "web: POST /api/usage — Cloud Monitoring gauge for the browser usage ring"
```

> Note for the reviewer: the exact Monitoring filter/aligner must match `Core.Publish/Services/UsageService.cs`. Read that file and reconcile the metric type, resource labels, and aggregation before approving — the C# version is the source of truth for the query.

---

### Task 3: `POST /api/analytics`

**Files:**
- Create: `web/api/analytics.ts`

**Interfaces:**
- Consumes: `getAccessToken`, http helpers.
- Produces: `POST /api/analytics` `{ saJson, sheetId }` → `200 { rows: [{ createdAt, tripName, maps, places, links }] }` or `{ error }`.

- [ ] **Step 1: Implement `analytics.ts`**

Port the read side of `SheetsAnalyticsService.FetchRowsAsync` (Sheets `values.get` on `A2:E`, columns Created At / Trip Name / Maps / Places / Map Links). `web/api/analytics.ts`:
```ts
import { getAccessToken } from "./_lib/googleAuth.js";
import { readJson, json, preflight } from "./_lib/http.js";

const SCOPE = "https://www.googleapis.com/auth/spreadsheets.readonly";

function sheetIdOf(raw: string): string {
  const m = raw.match(/\/spreadsheets\/d\/([a-zA-Z0-9-_]+)/);
  return m ? m[1] : raw.trim();
}

export default async function handler(req: Request): Promise<Response> {
  const pre = preflight(req);
  if (pre) return pre;
  if (req.method !== "POST") return json({ error: "POST only" }, 405);

  try {
    const { saJson, sheetId } = await readJson<{ saJson: any; sheetId: string }>(req);
    if (!saJson?.client_email || !sheetId) return json({ error: "bad input" }, 400);

    const id = sheetIdOf(sheetId);
    const token = await getAccessToken(saJson, SCOPE);
    const url = `https://sheets.googleapis.com/v4/spreadsheets/${id}/values/A2:E?majorDimension=ROWS`;
    const res = await fetch(url, { headers: { authorization: `Bearer ${token}` } });
    if (!res.ok) return json({ error: `sheets ${res.status}` }, 200);
    const data = (await res.json()) as { values?: string[][] };
    const rows = (data.values ?? [])
      .filter((r) => r.length && r[0])
      .map((r) => ({
        createdAt: r[0] ?? "",
        tripName: r[1] ?? "",
        maps: Number(r[2] ?? 0),
        places: Number(r[3] ?? 0),
        links: r[4] ?? "",
      }));
    return json({ rows });
  } catch (e) {
    return json({ error: String((e as Error).message ?? e) }, 200);
  }
}
```

- [ ] **Step 2: Type-check and commit**

Run: `cd web && npx tsc --noEmit` → no errors.
```bash
git add web/api/analytics.ts
git commit -m "web: POST /api/analytics — Sheets read for the browser Analytics page"
```

> Reviewer note: reconcile the range (`A2:E`), column order, and the sheet-id/URL parsing with `Core.Publish/Services/SheetsAnalyticsService.cs` (`SheetIdOf`, `FetchRowsAsync`). The C# service is the source of truth.

---

### Task 4: Browser client + re-enable SA/Sheet, wire usage & analytics

**Files:**
- Create: `src/GmapPlanner.App.Browser/BrowserApi.cs`
- Modify: `src/GmapPlanner.App.Browser/Platform/BrowserPlatformServices.cs`
- Modify: `src/GmapPlanner.UI/Platform/IPlatformServices.cs` (`PlatformFeatures`)
- Modify: `src/GmapPlanner.UI/ViewModels/MainViewModel.cs` (revert phase-2 SA/Sheet gating)
- Add DTOs to `src/GmapPlanner.Core/Json/GmapPlannerJsonContext.cs`
- Test: `tests/GmapPlanner.Core.Tests/BrowserApiContractTests.cs` (DTO (de)serialization via the source-gen context)

**Interfaces:**
- Consumes: `POST /api/usage`, `POST /api/analytics`.
- Produces: `BrowserPlatformServices.GetUsageAsync`/`FetchAnalyticsAsync` return the same `UsageGauge?` / `IReadOnlyList<AnalyticsRow>?` the desktop returns, so the VM and view are unchanged below the seam.

- [ ] **Step 1: Decide the feature gate (design note, no code)**

Phase 2 set `BrowserPlatformServices.Features.Analytics = false`, which hid the usage ring, the Analytics page nav, and the SA-JSON / Sheet-id Settings fields, and made `MainViewModel.LoadSetupBundleAsync` drop `GCP_SA_JSON`/`ANALYTICS_SHEET_ID`. Phase 3 makes the browser analytics-capable, so:
- Flip `BrowserPlatformServices.Features.Analytics` to `true`.
- The phase-2 guard in `LoadSetupBundleAsync` (`if (!Features.Analytics) { remove GCP_SA_JSON/ANALYTICS_SHEET_ID }`) then naturally allows the keys again — **no VM edit needed beyond confirming this**. Verify by reading the current `LoadSetupBundleAsync`.
- `Features.Publish` stays `false` (publishing is phase 4). Confirm no publish-only Settings control is bound to `Analytics`.

- [ ] **Step 2: Write the failing DTO round-trip test**

`tests/GmapPlanner.Core.Tests/BrowserApiContractTests.cs`:
```csharp
using System.Text.Json;
using GmapPlanner.Core.Json;
using Xunit;

public class BrowserApiContractTests
{
    [Fact]
    public void UsageResponse_roundtrips_through_source_gen_context()
    {
        var json = """{"used":1234,"limit":5000,"percent":25,"resetDays":9}""";
        var dto = JsonSerializer.Deserialize(json, GmapPlannerJsonContext.Default.UsageApiResponse);
        Assert.NotNull(dto);
        Assert.Equal(1234, dto!.Used);
        Assert.Equal(5000, dto.Limit);
        Assert.Equal(25, dto.Percent);
        Assert.Equal(9, dto.ResetDays);
    }

    [Fact]
    public void AnalyticsResponse_roundtrips_and_maps_rows()
    {
        var json = """{"rows":[{"createdAt":"2026-09-15 10:00","tripName":"Tokyo","maps":2,"places":11,"links":"a, b"}]}""";
        var dto = JsonSerializer.Deserialize(json, GmapPlannerJsonContext.Default.AnalyticsApiResponse);
        Assert.NotNull(dto);
        Assert.Single(dto!.Rows);
        Assert.Equal("Tokyo", dto.Rows[0].TripName);
        Assert.Equal(11, dto.Rows[0].Places);
    }
}
```

- [ ] **Step 3: Run it — fails (types missing)**

Run: `dotnet test tests/GmapPlanner.Core.Tests --filter BrowserApiContractTests`
Expected: compile failure — `UsageApiResponse`/`AnalyticsApiResponse` not defined.

- [ ] **Step 4: Add the DTOs + source-gen entries**

In `src/GmapPlanner.Core/Json/` add a small DTO file (or reuse an existing models file) — records the API returns:
```csharp
namespace GmapPlanner.Core.Json;

public sealed record UsageApiResponse(int Used, int Limit, int Percent, int? ResetDays);
public sealed record AnalyticsApiRow(string CreatedAt, string TripName, int Maps, int Places, string Links);
public sealed record AnalyticsApiResponse(System.Collections.Generic.IReadOnlyList<AnalyticsApiRow> Rows);
```
Add to `GmapPlannerJsonContext` (JSON is camelCase from the API, so set the naming policy on the context or per-property):
```csharp
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(UsageApiResponse))]
[JsonSerializable(typeof(AnalyticsApiResponse))]
[JsonSerializable(typeof(AnalyticsApiRow))]
// ...existing [JsonSerializable] entries stay...
public partial class GmapPlannerJsonContext : JsonSerializerContext { }
```
> If `GmapPlannerJsonContext` already sets naming options, keep them consistent — the API returns camelCase; verify the existing context options don't conflict, and if they do, give these DTOs explicit `[JsonPropertyName]` attributes instead.

- [ ] **Step 5: Run the test — passes**

Run: `dotnet test tests/GmapPlanner.Core.Tests --filter BrowserApiContractTests`
Expected: PASS (2 tests).

- [ ] **Step 6: Implement `BrowserApi.cs`**

`src/GmapPlanner.App.Browser/BrowserApi.cs`:
```csharp
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using GmapPlanner.Core.Json;
using GmapPlanner.Core.Services; // UsageGauge, AnalyticsRow

namespace GmapPlanner.App.Browser;

/// <summary>Typed client for the same-origin Vercel API functions (/api/usage, /api/analytics).</summary>
internal sealed class BrowserApi
{
    private readonly HttpClient _http;
    public BrowserApi(HttpClient http) => _http = http;

    public async Task<UsageGauge?> GetUsageAsync(string saJson, CancellationToken ct)
    {
        try
        {
            using var res = await _http.PostAsJsonAsync("/api/usage",
                new { saJson = ParseSa(saJson) }, GmapPlannerJsonContext.Default.Options, ct);
            if (!res.IsSuccessStatusCode) return null;
            var dto = await res.Content.ReadFromJsonAsync(GmapPlannerJsonContext.Default.UsageApiResponse, ct);
            if (dto is null) return null;
            return new UsageGauge(dto.Used, dto.Limit, dto.Percent, dto.ResetDays);
        }
        catch { return null; } // best-effort: hide the ring
    }

    public async Task<IReadOnlyList<AnalyticsRow>?> FetchAnalyticsAsync(string saJson, string sheetId, CancellationToken ct)
    {
        try
        {
            using var res = await _http.PostAsJsonAsync("/api/analytics",
                new { saJson = ParseSa(saJson), sheetId }, GmapPlannerJsonContext.Default.Options, ct);
            if (!res.IsSuccessStatusCode) return null;
            var dto = await res.Content.ReadFromJsonAsync(GmapPlannerJsonContext.Default.AnalyticsApiResponse, ct);
            if (dto is null) return null;
            return dto.Rows.Select(r => new AnalyticsRow(r.CreatedAt, r.TripName, r.Maps, r.Places, r.Links)).ToList();
        }
        catch { return null; }
    }

    // The SA JSON must go into the body as a JSON object, not a string. Parse it once.
    private static JsonElement ParseSa(string saJson) => JsonDocument.Parse(saJson).RootElement.Clone();
}
```
> `UsageGauge`/`AnalyticsRow` and the `new { ... }` anonymous payload: anonymous types can't use the source-gen context. Since the WASM app re-enables reflection STJ (`GmapPlanner.App.csproj` did the same for Playwright), confirm the Browser csproj allows reflection serialization for the *request* bodies, OR replace the anonymous payloads with small `record` request DTOs added to the source-gen context. **Prefer request DTOs** (`UsageApiRequest(JsonElement SaJson)`, `AnalyticsApiRequest(JsonElement SaJson, string SheetId)`) added to `GmapPlannerJsonContext` so no reflection is needed — align with trimming rule #1. Update this step to use those DTOs.

- [ ] **Step 7: Wire `BrowserPlatformServices`**

In `BrowserPlatformServices.cs`: construct one `HttpClient` with `BaseAddress = new Uri(<site origin>)` (in the browser, a relative `/api/...` against `HttpClient` needs a base address — use the page origin, available via a tiny `[JSImport]` returning `globalThis.location.origin`, or configure `HttpClient` to use the browser fetch which resolves relative URLs). Then:
```csharp
public Task<UsageGauge?> GetUsageAsync(string saJson, CancellationToken ct)
    => _api.GetUsageAsync(saJson, ct);

public Task<IReadOnlyList<AnalyticsRow>?> FetchAnalyticsAsync(string saJson, string sheetId, CancellationToken ct)
    => _api.FetchAnalyticsAsync(saJson, sheetId, ct);
```
Flip `Features` to `Analytics: true`. Add the `[JSImport]` origin helper to `wwwroot/main.js` (`globalThis.gmapPlanner.origin = () => location.origin;`).

- [ ] **Step 8: Build both hosts, run tests**

Run: `dotnet build src/GmapPlanner.App` → 0 errors.
Run: `dotnet build src/GmapPlanner.App.Browser` → 0 errors.
Run: `dotnet test tests/GmapPlanner.Core.Tests` → all green.

- [ ] **Step 9: Commit**

```bash
git add src/GmapPlanner.App.Browser/ src/GmapPlanner.UI/ src/GmapPlanner.Core/Json/ tests/
git commit -m "Browser: usage ring + Analytics via the Vercel API (Analytics feature on)"
```

---

### Task 5: Deploy the functions with the site

**Files:**
- Modify: `.github/workflows/web.yml`
- Create/confirm: `web/vercel.json` (Task 1)
- Modify: `CLAUDE.md`

**Interfaces:**
- Consumes: Task 1–3 functions under `web/api/`, Task 4 wwwroot build.
- Produces: a single Vercel deployment that serves the static WASM site **and** the `/api/*` functions.

- [ ] **Step 1: Assemble one deploy root**

The current workflow deploys `publish/browser/wwwroot`. Now the deploy must include `web/api/`. Change the deploy step to build the static site into `web/public/` and deploy `web/`:
```yaml
      - name: Publish browser app
        run: dotnet publish src/GmapPlanner.App.Browser -c Release -o publish/browser

      - name: Assemble Vercel deploy root
        run: |
          rm -rf web/public && mkdir -p web/public
          cp -r publish/browser/wwwroot/* web/public/

      - name: Deploy to Vercel
        env:
          VERCEL_TOKEN: ${{ secrets.VERCEL_TOKEN }}
          VERCEL_ORG_ID: ${{ secrets.VERCEL_ORG_ID }}
          VERCEL_PROJECT_ID: ${{ secrets.VERCEL_PROJECT_ID }}
        run: |
          TOKEN=$(printf '%s' "$VERCEL_TOKEN" | tr -d '\r\n')
          export VERCEL_ORG_ID=$(printf '%s' "$VERCEL_ORG_ID" | tr -d '\r\n')
          export VERCEL_PROJECT_ID=$(printf '%s' "$VERCEL_PROJECT_ID" | tr -d '\r\n')
          PROD_FLAG=""
          if [ "$GITHUB_REF" = "refs/heads/main" ]; then PROD_FLAG="--prod"; fi
          URL=$(npx --yes vercel@59 deploy web $PROD_FLAG --yes --token "$TOKEN")
          echo "Deployed: $URL"
          echo "### Deployed to $URL" >> "$GITHUB_STEP_SUMMARY"
```
Add `web/vercel.json`:
```json
{
  "$schema": "https://openapi.vercel.sh/vercel.json",
  "functions": { "api/*.ts": { "runtime": "@vercel/node@5" } },
  "outputDirectory": "public"
}
```
Add `web/public/` to `.gitignore` (built artifact).

- [ ] **Step 2: Document in CLAUDE.md**

Add a "Cloud API (phase 3)" subsection under the Browser host: the two functions, that the SA JSON is per-request (no stored secret), and the new deploy shape (`web/` root with `public/` static + `api/` functions, still `vercel@59`).

- [ ] **Step 3: Commit**

```bash
git add .github/workflows/web.yml web/vercel.json .gitignore CLAUDE.md
git commit -m "Deploy the Vercel API functions alongside the static browser host"
```

- [ ] **Step 4: Manual checklist (controller + user, on the deployed preview)**

1. Settings: paste an SA JSON + Sheet id; reload → they persist.
2. Usage ring appears and shows a plausible Places count (Cloud Monitoring reachable, CORS ok).
3. Analytics page lists the Sheet's rows.
4. A bad SA JSON hides the ring / shows "configure it" — never an error dialog.
5. Publish controls are still absent (phase 4 not built).

---

## Self-Review

- **Spec coverage:** `/api/usage`, `/api/analytics` (spec §Architecture, Build order #3) ✅. SA-in-body secret model (spec §Secrets) ✅. Usage ring + Analytics kept features (spec Decisions) ✅.
- **Placeholders:** the two "reviewer note" reconciliation steps (Tasks 2, 3) are deliberate — the C# services are the source of truth for the exact Monitoring filter and Sheets range; the implementer must read them, not invent them. Task 4 Step 6 flags the anonymous-payload-vs-source-gen decision with a concrete resolution (request DTOs).
- **Type consistency:** `UsageGauge`/`AnalyticsRow` are the existing Core types (constructor arity must match — the implementer verifies against `Core/Services/UsageGauge.cs` / `AnalyticsSheet.cs`).
- **Trimming:** request/response DTOs go through the source-gen context; the anonymous-type risk is called out with the DTO fix.
