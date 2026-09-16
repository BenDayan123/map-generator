import { put, del, list } from "@vercel/blob";

// Thin wrapper over @vercel/blob keyed by a stable pathname (addRandomSuffix off), so a job's
// blob can be read/deleted later by id alone. Stores ciphertext only. Uses BLOB_READ_WRITE_TOKEN
// from the environment (Vercel injects it). Low volume, so resolving a pathname->url via list is fine.

const PUT_OPTS = {
  access: "public" as const,
  addRandomSuffix: false,
  allowOverwrite: true,
  contentType: "text/plain",
};

export async function putBlob(pathname: string, content: string): Promise<void> {
  await put(pathname, content, PUT_OPTS);
}

/** The blob's public URL, or null if it doesn't exist. */
async function urlFor(pathname: string): Promise<string | null> {
  const { blobs } = await list({ prefix: pathname, limit: 1 });
  const hit = blobs.find((b) => b.pathname === pathname);
  return hit?.url ?? null;
}

/** The blob's text content, or null if missing. */
export async function getBlob(pathname: string): Promise<string | null> {
  const url = await urlFor(pathname);
  if (!url) return null;
  // Cache-bust: a just-overwritten blob can be briefly served stale from the edge.
  const res = await fetch(`${url}?t=${Date.now()}`, { cache: "no-store" });
  return res.ok ? await res.text() : null;
}

/** Deletes the blob if present (no-op otherwise). */
export async function delBlob(pathname: string): Promise<void> {
  const url = await urlFor(pathname);
  if (url) await del(url);
}

/** Deletes every blob under prefix older than maxAgeMs. Returns how many were removed. */
export async function deleteOlderThan(prefix: string, maxAgeMs: number): Promise<number> {
  const { blobs } = await list({ prefix });
  const cutoff = Date.now() - maxAgeMs;
  const stale = blobs.filter((b) => new Date(b.uploadedAt).getTime() < cutoff).map((b) => b.url);
  if (stale.length > 0) await del(stale);
  return stale.length;
}
