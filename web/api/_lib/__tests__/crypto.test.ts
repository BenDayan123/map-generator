import { describe, it, expect } from "vitest";
import { encrypt, decrypt, hmac, verifyHmac } from "../crypto.js";

const KEY = "00".repeat(32); // 32-byte hex

describe("crypto", () => {
  it("AES-256-GCM round-trips", () => {
    const pt = JSON.stringify({ hello: "world", n: 5 });
    const ct = encrypt(pt, KEY);
    expect(ct).not.toContain("world");
    expect(decrypt(ct, KEY)).toBe(pt);
  });

  it("each encryption uses a fresh IV", () => {
    expect(encrypt("same", KEY)).not.toBe(encrypt("same", KEY));
  });

  it("decrypt rejects a tampered tag", () => {
    const ct = encrypt("secret", KEY).split(".");
    ct[1] = Buffer.from("tampered").toString("base64url");
    expect(() => decrypt(ct.join("."), KEY)).toThrow();
  });

  it("decrypt rejects the wrong key", () => {
    const ct = encrypt("secret", KEY);
    expect(() => decrypt(ct, "11".repeat(32))).toThrow();
  });

  it("HMAC accepts a matching signature and rejects a wrong one", () => {
    const sig = hmac("body", "s3cret");
    expect(verifyHmac("body", "s3cret", sig)).toBe(true);
    expect(verifyHmac("body", "s3cret", hmac("body", "other"))).toBe(false);
  });

  it("verifyHmac is safe against a malformed signature", () => {
    expect(verifyHmac("body", "s3cret", "not-hex")).toBe(false);
    expect(verifyHmac("body", "s3cret", "")).toBe(false);
  });
});
