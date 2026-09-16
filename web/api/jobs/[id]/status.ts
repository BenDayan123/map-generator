import { json, jobIdFromUrl } from "../../_lib/http.js";
import { encrypt, verifyHmac } from "../../_lib/crypto.js";
import { putBlob } from "../../_lib/blob.js";

// POST /api/jobs/:id/status — the worker reports progress/result (HMAC over the raw body).
// A refreshedSession is re-encrypted before it rests in the status blob; the browser poll
// decrypts it back out on the final read.
async function handler(req: Request): Promise<Response> {
  if (req.method !== "POST") return json({ error: "POST only" }, 405);
  const secret = process.env.WORKER_HMAC;
  const jobKey = process.env.JOB_KEY;
  if (!secret || !jobKey) return json({ error: "server misconfigured" }, 500);

  const id = jobIdFromUrl(req);
  if (!id) return json({ error: "no job id" }, 400);

  const raw = await req.text();
  const sig = req.headers.get("x-worker-signature") ?? "";
  if (!verifyHmac(raw, secret, sig)) return json({ error: "unauthorized" }, 401);

  let status: Record<string, unknown>;
  try {
    status = JSON.parse(raw);
  } catch {
    return json({ error: "bad input" }, 400);
  }
  if (status.refreshedSession != null) {
    status.refreshedSession = encrypt(JSON.stringify(status.refreshedSession), jobKey);
  }
  await putBlob(`status/${id}`, JSON.stringify(status));
  return json({ ok: true });
}

export default { fetch: handler };
