import { getAccessToken } from "./_lib/googleAuth.js";
import { readJson, json, preflight } from "./_lib/http.js";

// Mirrors GmapPlanner.Core.Publish/Services/SheetsAnalyticsService.cs (FetchRowsAsync) and
// GmapPlanner.Core/Services/AnalyticsSheet.cs (SheetIdOf) — keep in sync.
const SCOPE = "https://www.googleapis.com/auth/spreadsheets.readonly";

interface SaJson {
  client_email?: string;
  private_key?: string;
  token_uri?: string;
}

/** Ports AnalyticsSheet.SheetIdOf: accepts a bare id or a full spreadsheet URL. */
function sheetIdOf(raw: string): string {
  const trimmed = raw.trim();
  const marker = "/spreadsheets/d/";
  const i = trimmed.indexOf(marker);
  return i < 0 ? trimmed : trimmed.slice(i + marker.length).split("/", 2)[0];
}

/** Ports SheetsAnalyticsService.Canonical: a header cell as a stable key. */
function canonical(title: string): string {
  return title
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]/g, "_")
    .replace(/^_+|_+$/g, "")
    .replace(/__/g, "_");
}

/** Integer-only parse, like C#'s int.TryParse — rejects "3.5", "", "abc". */
function parseIntStrict(s: string): number | null {
  return /^-?\d+$/.test(s) ? Number(s) : null;
}

export default async function handler(req: Request): Promise<Response> {
  const pre = preflight(req);
  if (pre) return pre;
  if (req.method !== "POST") return json({ error: "POST only" }, 405);

  let saJson: SaJson;
  let sheetId: string;
  try {
    const body = await readJson<{ saJson: SaJson; sheetId: string }>(req);
    saJson = body?.saJson;
    sheetId = body?.sheetId;
  } catch {
    return json({ error: "bad input" }, 400);
  }
  if (!saJson?.client_email || !sheetId || typeof sheetId !== "string") return json({ error: "bad input" }, 400);

  const id = sheetIdOf(sheetId);
  if (!id) return json({ error: "bad input" }, 400);

  try {
    const token = await getAccessToken(
      saJson as { client_email: string; private_key: string; token_uri?: string },
      SCOPE,
    );
    const url = `https://sheets.googleapis.com/v4/spreadsheets/${encodeURIComponent(id)}/values/A1:E`;
    const res = await fetch(url, { headers: { authorization: `Bearer ${token}` } });
    if (!res.ok) return json({ error: `sheets ${res.status}` }, 502);

    const data = (await res.json()) as { values?: string[][] };
    const values = data.values ?? [];
    if (values.length === 0) return json({ rows: [] });

    const keys = values[0].map((cell) => canonical(cell ?? ""));
    const rows: { createdAt: string; tripName: string; maps: number; places: number; links: string[] }[] = [];

    for (const raw of values.slice(1)) {
      const record: Record<string, string> = {};
      for (let i = 0; i < keys.length && i < raw.length; i++) record[keys[i]] = raw[i] ?? "";

      const createdAt = record["created_at"] ?? "";
      if (!createdAt.trim()) continue; // spacer / stray summary cell

      const links = (record["map_links"] ?? "")
        .split("\n")
        .map((l) => l.trim())
        .filter((l) => l.length > 0);
      const maps = parseIntStrict(record["maps"] ?? "") ?? links.filter((l) => l.startsWith("http")).length;
      const places = parseIntStrict(record["places"] ?? "") ?? 0;

      rows.push({ createdAt, tripName: record["trip_name"] ?? "", maps, places, links });
    }

    return json({ rows });
  } catch (e) {
    return json({ error: String((e as Error).message ?? e) }, 502);
  }
}
