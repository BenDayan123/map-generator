import { describe, it, expect } from "vitest";
import { generateKeyPairSync } from "node:crypto";
import { signJwt } from "../googleAuth.js";

function testSaJson() {
  const { privateKey } = generateKeyPairSync("rsa", { modulusLength: 2048 });
  const pem = privateKey.export({ type: "pkcs8", format: "pem" }).toString();
  return { client_email: "svc@proj.iam.gserviceaccount.com", private_key: pem, token_uri: "https://oauth2.googleapis.com/token" };
}

describe("signJwt", () => {
  it("produces a three-part JWT with the requested scope and issuer", () => {
    const sa = testSaJson();
    const jwt = signJwt(sa, "https://www.googleapis.com/auth/monitoring.read");
    const [h, p] = jwt.split(".");
    expect(jwt.split(".")).toHaveLength(3);
    const header = JSON.parse(Buffer.from(h, "base64url").toString());
    const claims = JSON.parse(Buffer.from(p, "base64url").toString());
    expect(header).toMatchObject({ alg: "RS256", typ: "JWT" });
    expect(claims.iss).toBe(sa.client_email);
    expect(claims.scope).toBe("https://www.googleapis.com/auth/monitoring.read");
    expect(claims.aud).toBe(sa.token_uri);
    expect(claims.exp - claims.iat).toBe(3600);
  });
});
