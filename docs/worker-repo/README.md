# Cloud publish worker (phase 4)

Google My Maps has no import API, so publishing drives the editor with Playwright. That can't
run in a Vercel function, so it runs as a **GitHub Actions job** in a private repo you own. The
browser submits an encrypted job to the site; the site dispatches this workflow; the worker
claims the job, publishes, and reports back.

## One-time setup

1. **Create a private GitHub repo** (e.g. `map-publish-worker`). Copy `publish.yml` into it as
   `.github/workflows/publish.yml`.

2. **Generate two secrets:**
   - `JOB_KEY` — 32 random bytes as hex: `openssl rand -hex 32`
   - `WORKER_HMAC` — any long random string: `openssl rand -hex 32`

3. **Vercel project env** (Settings → Environment Variables), all environments:

   | Name | Value |
   |------|-------|
   | `JOB_KEY` | the 32-byte hex from step 2 |
   | `WORKER_HMAC` | the random string from step 2 |
   | `WORKER_REPO` | `you/map-publish-worker` (owner/name of the private repo) |
   | `GITHUB_TOKEN` | a fine-grained PAT with **Actions: read and write** on the worker repo **only** |
   | `BLOB_READ_WRITE_TOKEN` | from a Vercel Blob store (Storage → create a Blob store; it's injected automatically once the store is linked to the project) |

4. **Private worker repo secrets** (its Settings → Secrets and variables → Actions):

   | Name | Value |
   |------|-------|
   | `API_BASE_URL` | the deployed site origin, e.g. `https://my-maps-generator.vercel.app` |
   | `WORKER_HMAC` | the **same** value as the Vercel `WORKER_HMAC` |

5. **Produce a session** on your computer with the login helper, then load it on the site's
   Settings page:
   ```bash
   dotnet run --project src/GmapPlanner.LoginHelper -- ./credentials.json
   # writes session.json — drop it into the web app's Settings → Cloud publishing session
   ```
   `credentials.json` is an OAuth **Desktop** client (Drive API enabled).

## Abuse mitigations

- `POST /api/jobs` rejects bodies over 2 MB and more than 10 KML files (`413`).
- Add a **Vercel Firewall** rate-limit rule on `/api/jobs` (e.g. 10 requests/hour/IP) —
  Project → Firewall → Rate limiting. Submitting needs only the site URL, so this caps abuse.
- Job/status blobs are ciphertext, and `/api/cron/cleanup` deletes anything older than an hour.

## Known risk — session replay

GitHub runners use datacenter IPs; Google may re-challenge a replayed session. The worker surfaces
this as `SESSION_EXPIRED` (the site tells you to re-run the login helper) rather than crashing. This
design makes the failure clean; it does not defeat the re-challenge. If it happens often, run the
worker somewhere with a more residential-looking egress (e.g. **Google Cloud Run Jobs** dispatched
the same way) instead of GitHub Actions — the worker binary and HMAC contract are identical.
