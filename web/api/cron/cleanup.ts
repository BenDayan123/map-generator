import { deleteOlderThan } from "../_lib/blob.js";

// Daily GC: encrypted job/status blobs should be short-lived (a job runs in minutes and the
// browser reads its final status once). Anything older than an hour is abandoned — delete it.
const MAX_AGE_MS = 60 * 60 * 1000;

async function handler(_req: Request): Promise<Response> {
  const jobs = await deleteOlderThan("jobs/", MAX_AGE_MS);
  const status = await deleteOlderThan("status/", MAX_AGE_MS);
  return new Response(JSON.stringify({ deleted: { jobs, status } }), {
    headers: { "content-type": "application/json" },
  });
}

export default { fetch: handler };
