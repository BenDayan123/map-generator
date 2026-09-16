import { createCipheriv, createDecipheriv, createHmac, randomBytes, timingSafeEqual } from "node:crypto";

// AES-256-GCM job encryption + HMAC-SHA256 request auth. Token format: iv.tag.ciphertext,
// each part base64url, dot-joined. Key is 32-byte hex (JOB_KEY); IV is a fresh 12 bytes per call.
const IV_LEN = 12;

function keyBuf(keyHex: string): Buffer {
  const k = Buffer.from(keyHex, "hex");
  if (k.length !== 32) throw new Error("key must be 32 bytes (64 hex chars)");
  return k;
}

export function encrypt(plaintext: string, keyHex: string): string {
  const iv = randomBytes(IV_LEN);
  const cipher = createCipheriv("aes-256-gcm", keyBuf(keyHex), iv);
  const ct = Buffer.concat([cipher.update(plaintext, "utf8"), cipher.final()]);
  const tag = cipher.getAuthTag();
  return [iv, tag, ct].map((b) => b.toString("base64url")).join(".");
}

export function decrypt(token: string, keyHex: string): string {
  const parts = token.split(".");
  if (parts.length !== 3) throw new Error("malformed token");
  const [iv, tag, ct] = parts.map((p) => Buffer.from(p, "base64url"));
  const decipher = createDecipheriv("aes-256-gcm", keyBuf(keyHex), iv);
  decipher.setAuthTag(tag); // final() throws if the tag/key/iv don't authenticate
  return Buffer.concat([decipher.update(ct), decipher.final()]).toString("utf8");
}

export function hmac(body: string, secret: string): string {
  return createHmac("sha256", secret).update(body, "utf8").digest("hex");
}

export function verifyHmac(body: string, secret: string, provided: string): boolean {
  const expected = hmac(body, secret);
  // timingSafeEqual throws on length mismatch; a malformed/wrong-length signature is just a reject.
  if (provided.length !== expected.length) return false;
  try {
    return timingSafeEqual(Buffer.from(provided, "hex"), Buffer.from(expected, "hex"));
  } catch {
    return false;
  }
}
