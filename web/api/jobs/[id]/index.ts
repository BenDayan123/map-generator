import { json, preflight, jobIdFromUrl } from "../../_lib/http.js";
import { decrypt } from "../../_lib/crypto.js";
import { getBlob, delBlob } from "../../_lib/blob.js";

// GET /api/jobs/:id — the browser polls status (the id is the capability, so no HMAC).
// On a terminal state the encrypted refreshedSession is decrypted back for this final read
// and the status/job blobs are deleted.
async function handler(req: Request): Promise<Response> {
  const pre = preflight(req);
  if (pre) return pre;
  if (req.method !== "GET") return json({ error: "GET only" }, 405);

  const jobKey = process.env.JOB_KEY;
  if (!jobKey) return json({ error: "server misconfigured" }, 500);

  const id = jobIdFromUrl(req);
  if (!id) return json({ error: "no job id" }, 400);

  const stored = await getBlob(`status/${id}`);
  if (stored === null) return json({ error: "unknown job" }, 404);

  let status: Record<string, unknown>;
  try {
    status = JSON.parse(stored);
  } catch {
    return json({ error: "corrupt status" }, 502);
  }

  const terminal = status.state === "done" || status.state === "failed";
  if (terminal && typeof status.refreshedSession === "string") {
    try {
      status.refreshedSession = JSON.parse(decrypt(status.refreshedSession, jobKey));
    } catch {
      status.refreshedSession = null; // don't fail the whole poll over a bad session blob
    }
  }
  if (terminal) {
    await delBlob(`status/${id}`).catch(() => {});
    await delBlob(`jobs/${id}`).catch(() => {}); // usually already gone (claimed)
  }
  return json(status);
}

export default { fetch: handler };
