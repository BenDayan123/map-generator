import { json, jobIdFromUrl } from "../../_lib/http.js";
import { decrypt, verifyHmac } from "../../_lib/crypto.js";
import { getBlob, delBlob } from "../../_lib/blob.js";

// POST /api/jobs/:id/claim — the worker authenticates with HMAC over the raw body (empty here),
// receives the decrypted payload, and the job blob is deleted so it can be claimed only once.
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

  const ct = await getBlob(`jobs/${id}`);
  if (ct === null) return json({ error: "already claimed or unknown" }, 404);
  await delBlob(`jobs/${id}`); // single claim

  let plaintext: string;
  try {
    plaintext = decrypt(ct, jobKey);
  } catch {
    return json({ error: "decrypt failed" }, 500);
  }
  return new Response(plaintext, {
    status: 200,
    headers: { "content-type": "application/json" },
  });
}

export default { fetch: handler };
