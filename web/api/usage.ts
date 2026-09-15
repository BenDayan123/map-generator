import { getAccessToken } from "./_lib/googleAuth.js";
import { readJson, json, preflight } from "./_lib/http.js";

// Mirrors GmapPlanner.Core.Publish/Services/UsageService.cs — keep in sync.
const LIMIT = 5000; // AppConfig.GeoMonthlyLimit — Places Text Search Pro free cap
const SCOPE = "https://www.googleapis.com/auth/monitoring.read"; // AppConfig.MonitoringScope
const SERVICE = "places.googleapis.com"; // AppConfig.GeocodeService
const ZONE = "America/Los_Angeles"; // Google quotas reset on Pacific time

interface SaJson {
  client_email?: string;
  project_id?: string;
  private_key?: string;
  token_uri?: string;
}

/** y/m/d (1-based month) of `date` as seen in `ZONE`. */
function pacificDate(date: Date): { year: number; month: number; day: number } {
  const parts = new Intl.DateTimeFormat("en-US", {
    timeZone: ZONE,
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
  }).formatToParts(date);
  const get = (t: string) => Number(parts.find((p) => p.type === t)?.value);
  return { year: get("year"), month: get("month"), day: get("day") };
}

/** UTC instant for Pacific midnight on the given calendar date (DST-safe, no tz database needed). */
function pacificMidnightUtc(year: number, month: number, day: number): Date {
  // First guess: treat the Pacific fields as if they were UTC, then correct by the
  // actual Pacific UTC offset at that instant (7h or 8h depending on DST).
  const guess = new Date(Date.UTC(year, month - 1, day));
  const asPacific = new Intl.DateTimeFormat("en-US", {
    timeZone: ZONE,
    hourCycle: "h23",
    year: "numeric",
    month: "2-digit",
    day: "2-digit",
    hour: "2-digit",
    minute: "2-digit",
  }).formatToParts(guess);
  const get = (t: string) => Number(asPacific.find((p) => p.type === t)?.value);
  const guessAsPacificWallClock = Date.UTC(get("year"), get("month") - 1, get("day"), get("hour"), get("minute"));
  const offsetMs = guessAsPacificWallClock - guess.getTime();
  return new Date(guess.getTime() - offsetMs);
}

function rfc3339(date: Date): string {
  return date.toISOString().replace(/\.\d{3}Z$/, "Z");
}

export default async function handler(req: Request): Promise<Response> {
  const pre = preflight(req);
  if (pre) return pre;
  if (req.method !== "POST") return json({ error: "POST only" }, 405);

  try {
    const { saJson } = await readJson<{ saJson: SaJson }>(req);
    if (!saJson?.client_email || !saJson?.project_id) return json({ error: "bad sa json" }, 400);

    const token = await getAccessToken(saJson as { client_email: string; private_key: string; token_uri?: string }, SCOPE);

    const now = new Date();
    const { year, month, day } = pacificDate(now);
    const monthStart = pacificMidnightUtc(year, month, 1);
    const windowSeconds = Math.max(Math.round((now.getTime() - monthStart.getTime()) / 1000), 60);

    const filter =
      'metric.type="serviceruntime.googleapis.com/api/request_count" ' +
      'AND resource.type="consumed_api" ' +
      `AND resource.label."service"="${SERVICE}"`;
    const url =
      `https://monitoring.googleapis.com/v3/projects/${encodeURIComponent(saJson.project_id)}/timeSeries` +
      `?filter=${encodeURIComponent(filter)}` +
      `&interval.startTime=${encodeURIComponent(rfc3339(monthStart))}` +
      `&interval.endTime=${encodeURIComponent(rfc3339(now))}` +
      `&aggregation.alignmentPeriod=${windowSeconds}s` +
      `&aggregation.perSeriesAligner=ALIGN_SUM` +
      `&aggregation.crossSeriesReducer=REDUCE_SUM`;

    const res = await fetch(url, { headers: { authorization: `Bearer ${token}` } });
    if (!res.ok) return json({ error: `monitoring ${res.status}` }, 502);
    const data = (await res.json()) as {
      timeSeries?: { points?: { value?: { int64Value?: string; doubleValue?: number } }[] }[];
    };

    let used = 0;
    for (const series of data.timeSeries ?? [])
      for (const pt of series.points ?? []) {
        const v = pt.value;
        if (!v) continue;
        if (typeof v.int64Value === "string" && v.int64Value.length > 0) used += Number(v.int64Value);
        else if (typeof v.doubleValue === "number") used += v.doubleValue;
      }

    // Whole calendar days from today (Pacific) to the 1st of next month (Pacific).
    const nextMonthYear = month === 12 ? year + 1 : year;
    const nextMonth = month === 12 ? 1 : month + 1;
    const resetDays = Math.round(
      (Date.UTC(nextMonthYear, nextMonth - 1, 1) - Date.UTC(year, month - 1, day)) / 86_400_000,
    );

    const percent = LIMIT <= 0 ? 0 : Math.min(100, Math.max(0, (used / LIMIT) * 100));
    return json({ used, limit: LIMIT, percent, resetDays });
  } catch (e) {
    return json({ error: String((e as Error).message ?? e) }, 502);
  }
}
