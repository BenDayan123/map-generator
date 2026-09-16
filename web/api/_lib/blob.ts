import { put, del, list, get } from "@vercel/blob";

// Thin wrapper over @vercel/blob keyed by a stable pathname (addRandomSuffix off), so a job's
// blob can be read/deleted later by id alone. The store is PRIVATE (blobs need the token to read);
// content is AES-GCM ciphertext regardless. Uses BLOB_READ_WRITE_TOKEN from the environment.

const ACCESS = "private" as const;

const PUT_OPTS = {
  access: ACCESS,
  addRandomSuffix: false,
  allowOverwrite: true,
  contentType: "text/plain",
};

export async function putBlob(pathname: string, content: string): Promise<void> {
  await put(pathname, content, PUT_OPTS);
}

/** The blob's text content, or null if missing. */
export async function getBlob(pathname: string): Promise<string | null> {
  // useCache: false — a just-overwritten status blob must not read back stale from the CDN.
  const res = await get(pathname, { access: ACCESS, useCache: false });
  if (!res || res.statusCode !== 200) return null;
  return await new Response(res.stream).text();
}

/** Deletes the blob if present (no-op otherwise). */
export async function delBlob(pathname: string): Promise<void> {
  try {
    await del(pathname);
  } catch {
    // Already gone — nothing to do.
  }
}

/** Deletes every blob under prefix older than maxAgeMs. Returns how many were removed. */
export async function deleteOlderThan(prefix: string, maxAgeMs: number): Promise<number> {
  const { blobs } = await list({ prefix });
  const cutoff = Date.now() - maxAgeMs;
  const stale = blobs.filter((b) => new Date(b.uploadedAt).getTime() < cutoff).map((b) => b.url);
  if (stale.length > 0) await del(stale);
  return stale.length;
}
