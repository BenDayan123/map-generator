import { randomUUID } from "node:crypto";
import { json, preflight } from "../_lib/http.js";
import { encrypt } from "../_lib/crypto.js";
import { putBlob } from "../_lib/blob.js";
import { dispatchWorker } from "../_lib/dispatch.js";

// POST /api/jobs — the browser submits an encrypted publish job. The plaintext payload
// (KMLs + recipients + the user's session.json + optional SA JSON/Sheet id) is encrypted
// into Blob storage; the worker repo is dispatched with only the job id. No auth beyond
// possession of the URL; abuse caps below are mandatory.
const MAX_BODY_BYTES = 2 * 1024 * 1024; // 2 MB
const MAX_KMLS = 10;

async function handler(req: Request): Promise<Response> {
  const pre = preflight(req);
  if (pre) return pre;
  if (req.method !== "POST") return json({ error: "POST only" }, 405);

  const jobKey = process.env.JOB_KEY;
  if (!jobKey) return json({ error: "server misconfigured" }, 500);

  const raw = await req.text();
  if (raw.length > MAX_BODY_BYTES) return json({ error: "job too large" }, 413);

  let payload: { kmls?: unknown[]; session?: unknown };
  try {
    payload = JSON.parse(raw);
  } catch {
    return json({ error: "bad input" }, 400);
  }
  if (!Array.isArray(payload.kmls) || payload.kmls.length === 0) return json({ error: "no kmls" }, 400);
  if (payload.kmls.length > MAX_KMLS) return json({ error: `too many KMLs (max ${MAX_KMLS})` }, 413);
  if (!payload.session) return json({ error: "no session" }, 400);

  const id = randomUUID();
  try {
    await putBlob(`jobs/${id}`, encrypt(raw, jobKey));
    await putBlob(`status/${id}`, JSON.stringify({ state: "queued" }));
    await dispatchWorker(id);
  } catch (e) {
    // Best effort: mark failed so the browser poll surfaces it instead of hanging.
    try {
      await putBlob(`status/${id}`, JSON.stringify({ state: "failed", error: "dispatch failed" }));
    } catch {
      /* ignore */
    }
    return json({ error: `dispatch failed: ${String((e as Error).message ?? e)}` }, 502);
  }
  return json({ id });
}

export default { fetch: handler };
