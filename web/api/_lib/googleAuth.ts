import { createSign } from "node:crypto";

interface SaJson { client_email: string; private_key: string; token_uri?: string; }

function b64url(input: Buffer | string): string {
  return Buffer.from(input).toString("base64url");
}

export function signJwt(sa: SaJson, scope: string): string {
  const iat = Math.floor(Date.now() / 1000);
  const aud = sa.token_uri ?? "https://oauth2.googleapis.com/token";
  const header = b64url(JSON.stringify({ alg: "RS256", typ: "JWT" }));
  const claims = b64url(JSON.stringify({ iss: sa.client_email, scope, aud, iat, exp: iat + 3600 }));
  const signingInput = `${header}.${claims}`;
  const signature = createSign("RSA-SHA256").update(signingInput).sign(sa.private_key, "base64url");
  return `${signingInput}.${signature}`;
}

export async function getAccessToken(sa: SaJson, scope: string): Promise<string> {
  const assertion = signJwt(sa, scope);
  const res = await fetch(sa.token_uri ?? "https://oauth2.googleapis.com/token", {
    method: "POST",
    headers: { "content-type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams({ grant_type: "urn:ietf:params:oauth:grant-type:jwt-bearer", assertion }),
  });
  if (!res.ok) throw new Error(`token exchange failed: ${res.status}`);
  const data = (await res.json()) as { access_token?: string };
  if (!data.access_token) throw new Error("token exchange returned no access_token");
  return data.access_token;
}
