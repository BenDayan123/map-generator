using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Google.Apis.Auth.OAuth2;

namespace GmapPlanner.Core.Services;

/// <summary>
/// Ports gmap_planner/analytics.py: append each generated trip to a Google Sheet and read it
/// back for the Analytics page. The Sheet outlives the local install and is human-readable in
/// the browser, so <see cref="ApplyLayoutAsync"/> also styles it (display header, banded rows,
/// a summary box of live formulas) — matching the Python original's layout.
///
/// Auth reuses the usage-gauge service-account JSON (Sheets API enabled, Sheet shared as Editor
/// with the SA email). Everything is best-effort: any failure returns null/empty so a Sheets
/// problem never breaks map generation. Uses raw REST + JsonNode (no Sheets SDK) to stay small.
/// </summary>
public sealed class SheetsAnalyticsService(HttpClient http)
{
    // Canonical key -> display title, in column order (A..E). Add a column by extending this.
    private static readonly (string Key, string Title)[] Columns =
    [
        ("created_at", "Created At"),
        ("trip_name", "Trip Name"),
        ("maps", "Maps"),
        ("places", "Places"),
        ("map_links", "Map Links"),
    ];

    private static readonly string[] Header = Columns.Select(c => c.Title).ToArray();
    private static string Col(int index) => ((char)('A' + index)).ToString();
    private static readonly string TableEndCol = Col(Columns.Length - 1);          // E
    private static readonly int LabelColIndex = Columns.Length + 1;                // G (one gutter col)
    private static readonly int ValueColIndex = LabelColIndex + 1;                 // H

    // Canonical key -> its column letter.
    private static string KeyCol(string key) => Col(Array.FindIndex(Columns, c => c.Key == key));

    // --- Public API ----------------------------------------------------------

    /// <summary>
    /// All logged rows, newest last. Returns null when the Sheet is unconfigured or unreachable
    /// (the page shows a "configure it" state) and an empty list when it's reachable but empty.
    /// </summary>
    public async Task<List<AnalyticsRow>?> FetchRowsAsync(string saJson, string sheetIdRaw, CancellationToken ct = default)
    {
        var sheetId = AnalyticsSheet.SheetIdOf(sheetIdRaw);
        if (string.IsNullOrWhiteSpace(saJson) || string.IsNullOrEmpty(sheetId)) return null;
        try
        {
            var token = await TokenAsync(saJson, ct);
            var body = await GetAsync($"{Base(sheetId)}/values/A1:{TableEndCol}", token, ct);
            var values = body?["values"] as JsonArray;
            if (values is null || values.Count == 0) return [];

            var keys = (values[0] as JsonArray ?? []).Select(v => Canonical(v?.GetValue<string>() ?? "")).ToList();
            var rows = new List<AnalyticsRow>();
            foreach (var raw in values.Skip(1))
            {
                var cells = (raw as JsonArray ?? []).Select(v => v?.ToString() ?? "").ToList();
                var record = new Dictionary<string, string>();
                for (var i = 0; i < keys.Count && i < cells.Count; i++) record[keys[i]] = cells[i];

                var createdAt = record.GetValueOrDefault("created_at", "");
                if (string.IsNullOrWhiteSpace(createdAt)) continue; // spacer / stray summary cell

                var links = record.GetValueOrDefault("map_links", "")
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToList();
                var maps = ParseInt(record.GetValueOrDefault("maps", "")) ?? links.Count(l => l.StartsWith("http"));
                var places = ParseInt(record.GetValueOrDefault("places", "")) ?? 0;
                rows.Add(new AnalyticsRow(createdAt, record.GetValueOrDefault("trip_name", ""), maps, places, links));
            }
            return rows;
        }
        catch
        {
            return null; // unreachable / permission / network — treated as "not available"
        }
    }

    /// <summary>
    /// Appends one row for a generated trip: time, name, map/place counts, and any map links.
    /// Never throws. Lays the Sheet out first so the header and summary box stay in place.
    /// </summary>
    public async Task RecordPublishAsync(
        string saJson, string sheetIdRaw, string tripName, int maps, int places, IEnumerable<string> mapLinks,
        CancellationToken ct = default)
    {
        var sheetId = AnalyticsSheet.SheetIdOf(sheetIdRaw);
        if (string.IsNullOrWhiteSpace(saJson) || string.IsNullOrEmpty(sheetId)) return;
        try
        {
            var token = await TokenAsync(saJson, ct);
            await ApplyLayoutAsync(sheetId, token, ct);

            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            var links = string.Join("\n", mapLinks.Where(l => !string.IsNullOrWhiteSpace(l)));
            var row = new JsonArray(now, SafeText(string.IsNullOrWhiteSpace(tripName) ? "(unnamed)" : tripName), maps, places, links);
            var payload = new JsonObject { ["values"] = new JsonArray(row) };

            // valueInputOption=USER_ENTERED so the timestamp lands as a real date the month
            // formulas can compare; the append range pins insertion to the table columns so a
            // row never lands under the summary box.
            var url = $"{Base(sheetId)}/values/A1:{TableEndCol}1:append?valueInputOption=USER_ENTERED&insertDataOption=INSERT_ROWS";
            await PostAsync(url, payload, token, ct);
        }
        catch
        {
            // Logging must never break a run.
        }
    }

    private async Task ApplyLayoutAsync(string sheetId, string token, CancellationToken ct)
    {
        // First tab's numeric sheetId + any existing banding id (banding must be updated, not re-added).
        var meta = await GetAsync(
            $"{Base(sheetId)}?fields=sheets(properties(sheetId),bandedRanges(bandedRangeId))", token, ct);
        var sheet = (meta?["sheets"] as JsonArray)?.FirstOrDefault();
        var tabId = sheet?["properties"]?["sheetId"]?.GetValue<int>();
        if (tabId is null) return;
        var bandingId = (sheet?["bandedRanges"] as JsonArray)?.FirstOrDefault()?["bandedRangeId"]?.GetValue<int>();

        // Already tidy? The header row matching is enough for a single-user desktop log.
        var head = await GetAsync($"{Base(sheetId)}/values/A1:{TableEndCol}1", token, ct);
        var first = (head?["values"] as JsonArray)?.FirstOrDefault() as JsonArray;
        if (first is not null && Header.SequenceEqual(first.Select(v => v?.GetValue<string>() ?? ""))) return;

        // Write the display header and the summary box (formulas), then apply formatting.
        await PutValuesAsync(sheetId, "A1", new JsonArray(new JsonArray(Header.Select(h => (JsonNode)h!).ToArray())), token, ct);
        await PutValuesAsync(sheetId, $"{Col(LabelColIndex)}1", SummaryRows(), token, ct);
        await PostAsync($"{Base(sheetId)}:batchUpdate", new JsonObject { ["requests"] = LayoutRequests(tabId.Value, bandingId) }, token, ct);
    }

    // --- Summary box + formatting (ports analytics.py's _SUMMARY / _layout_requests) ---------

    private static string DateColRange => $"${KeyCol("created_at")}$2:${KeyCol("created_at")}";
    private static string ThisMonthFilter =>
        $"{DateColRange},\">=\"&EOMONTH(TODAY(),-1)+1,{DateColRange},\"<\"&EOMONTH(TODAY(),0)+1";
    private static string SumCol(string key) => $"=SUM(${KeyCol(key)}$2:${KeyCol(key)})";
    private static string MonthCol(string key) => $"=SUMIFS(${KeyCol(key)}$2:${KeyCol(key)},{ThisMonthFilter})";

    private static (string Label, string Value)[] Summary =>
    [
        ("Summary", ""),
        ("Total publishes", $"=MAX(0,COUNTA({DateColRange}))"),
        ("Distinct trips",
            $"=IF(COUNTA(${KeyCol("trip_name")}$2:${KeyCol("trip_name")})=0,0," +
            $"COUNTUNIQUE(${KeyCol("trip_name")}$2:${KeyCol("trip_name")}))"),
        ("Maps created", SumCol("maps")),
        ("Places created", SumCol("places")),
        ("Maps this month", MonthCol("maps")),
        ("Places this month", MonthCol("places")),
        ("Publishes this month", $"=COUNTIFS({ThisMonthFilter})"),
        ("Latest publish",
            $"=IF(COUNT({DateColRange})=0,\"—\",TEXT(MAX({DateColRange}),\"yyyy-mm-dd hh:mm\"))"),
    ];

    private static JsonArray SummaryRows() =>
        new(Summary.Select(s => (JsonNode)new JsonArray(s.Label, s.Value)).ToArray());

    private static JsonObject Rgb(double r, double g, double b) =>
        new() { ["red"] = r, ["green"] = g, ["blue"] = b };

    private static readonly int[] ColumnWidths = [150, 220, 70, 70, 520]; // by column, then gutter + summary follow

    private static JsonArray LayoutRequests(int sheetId, int? bandingId)
    {
        var teal = Rgb(0.059, 0.463, 0.431);
        var white = Rgb(1, 1, 1);
        var band = Rgb(0.953, 0.976, 0.976);
        var tint = Rgb(0.925, 0.965, 0.957);
        var line = Rgb(0.784, 0.855, 0.847);

        JsonObject Range(int r0, int? r1, int c0, int c1)
        {
            var o = new JsonObject { ["sheetId"] = sheetId, ["startColumnIndex"] = c0, ["endColumnIndex"] = c1, ["startRowIndex"] = r0 };
            if (r1 is not null) o["endRowIndex"] = r1;
            return o;
        }

        JsonObject RepeatCell(JsonObject range, JsonObject format, string fields) => new()
        {
            ["repeatCell"] = new JsonObject { ["range"] = range, ["cell"] = new JsonObject { ["userEnteredFormat"] = format }, ["fields"] = fields },
        };

        var reqs = new JsonArray
        {
            // Freeze the header row.
            new JsonObject { ["updateSheetProperties"] = new JsonObject
            {
                ["properties"] = new JsonObject { ["sheetId"] = sheetId, ["gridProperties"] = new JsonObject { ["frozenRowCount"] = 1 } },
                ["fields"] = "gridProperties.frozenRowCount",
            } },
            // Teal, bold, white header.
            RepeatCell(Range(0, 1, 0, Header.Length), new JsonObject
            {
                ["backgroundColor"] = teal,
                ["verticalAlignment"] = "MIDDLE",
                ["wrapStrategy"] = "CLIP",
                ["textFormat"] = new JsonObject { ["bold"] = true, ["fontSize"] = 11, ["foregroundColor"] = white },
            }, "userEnteredFormat(backgroundColor,verticalAlignment,wrapStrategy,textFormat)"),
            // Header row a touch taller.
            new JsonObject { ["updateDimensionProperties"] = new JsonObject
            {
                ["range"] = new JsonObject { ["sheetId"] = sheetId, ["dimension"] = "ROWS", ["startIndex"] = 0, ["endIndex"] = 1 },
                ["properties"] = new JsonObject { ["pixelSize"] = 34 }, ["fields"] = "pixelSize",
            } },
            // Real datetimes in column A (the month formulas depend on it).
            RepeatCell(Range(1, null, 0, 1), new JsonObject
            {
                ["numberFormat"] = new JsonObject { ["type"] = "DATE_TIME", ["pattern"] = "yyyy-mm-dd hh:mm" },
            }, "userEnteredFormat.numberFormat"),
            // Body: top-aligned, links clipped so a multi-map cell doesn't stretch the row.
            RepeatCell(Range(1, null, 0, Header.Length), new JsonObject
            {
                ["verticalAlignment"] = "TOP", ["wrapStrategy"] = "CLIP",
            }, "userEnteredFormat(verticalAlignment,wrapStrategy)"),
            // Centre the count columns.
            RepeatCell(Range(1, null, 2, 4), new JsonObject { ["horizontalAlignment"] = "CENTER" }, "userEnteredFormat.horizontalAlignment"),
            // Filter over the table.
            new JsonObject { ["setBasicFilter"] = new JsonObject { ["filter"] = new JsonObject { ["range"] = Range(0, null, 0, Header.Length) } } },
        };

        // Column widths (table), plus the gutter + summary pair.
        void Width(int index, int px) => reqs.Add(new JsonObject { ["updateDimensionProperties"] = new JsonObject
        {
            ["range"] = new JsonObject { ["sheetId"] = sheetId, ["dimension"] = "COLUMNS", ["startIndex"] = index, ["endIndex"] = index + 1 },
            ["properties"] = new JsonObject { ["pixelSize"] = px }, ["fields"] = "pixelSize",
        } });
        for (var i = 0; i < ColumnWidths.Length; i++) Width(i, ColumnWidths[i]);
        Width(Columns.Length, 24);      // gutter
        Width(LabelColIndex, 190);
        Width(ValueColIndex, 110);

        // Banded rows (update an existing band, else add).
        var banded = new JsonObject
        {
            ["range"] = Range(0, null, 0, Header.Length),
            ["rowProperties"] = new JsonObject { ["headerColor"] = teal, ["firstBandColor"] = white, ["secondBandColor"] = band },
        };
        if (bandingId is null)
            reqs.Add(new JsonObject { ["addBanding"] = new JsonObject { ["bandedRange"] = banded } });
        else
        {
            banded["bandedRangeId"] = bandingId;
            reqs.Add(new JsonObject { ["updateBanding"] = new JsonObject { ["bandedRange"] = banded, ["fields"] = "*" } });
        }

        // Summary box: tinted title, bold labels, right-aligned values, a border around it.
        JsonObject Box(int r0, int? r1, int c1) => Range(r0, r1, LabelColIndex, c1);
        reqs.Add(RepeatCell(Box(0, Summary.Length, ValueColIndex + 1), new JsonObject { ["backgroundColor"] = tint }, "userEnteredFormat.backgroundColor"));
        reqs.Add(RepeatCell(Box(0, 1, ValueColIndex + 1), new JsonObject
        {
            ["backgroundColor"] = teal,
            ["textFormat"] = new JsonObject { ["bold"] = true, ["fontSize"] = 11, ["foregroundColor"] = white },
        }, "userEnteredFormat(backgroundColor,textFormat)"));
        reqs.Add(RepeatCell(Box(1, Summary.Length, ValueColIndex), new JsonObject
        {
            ["textFormat"] = new JsonObject { ["bold"] = true },
        }, "userEnteredFormat.textFormat"));
        reqs.Add(RepeatCell(Range(1, Summary.Length, ValueColIndex, ValueColIndex + 1), new JsonObject
        {
            ["horizontalAlignment"] = "RIGHT",
        }, "userEnteredFormat.horizontalAlignment"));
        var border = new JsonObject { ["style"] = "SOLID", ["color"] = line };
        reqs.Add(new JsonObject { ["updateBorders"] = new JsonObject
        {
            ["range"] = Box(0, Summary.Length, ValueColIndex + 1),
            ["top"] = border.DeepClone(), ["bottom"] = border.DeepClone(),
            ["left"] = border.DeepClone(), ["right"] = border.DeepClone(),
            ["innerHorizontal"] = border.DeepClone(),
        } });

        return reqs;
    }

    // --- REST plumbing -------------------------------------------------------

    private static string Base(string sheetId) => string.Format(AppConfig.SheetsApiBase, sheetId);

    private static async Task<string> TokenAsync(string saJson, CancellationToken ct)
    {
        var credential = GoogleCredential.FromJson(saJson).CreateScoped(AppConfig.SheetsScope);
        return await ((ITokenAccess)credential).GetAccessTokenForRequestAsync(cancellationToken: ct);
    }

    private async Task PutValuesAsync(string sheetId, string a1, JsonArray values, string token, CancellationToken ct)
    {
        var url = $"{Base(sheetId)}/values/{a1}?valueInputOption=USER_ENTERED";
        await SendAsync(HttpMethod.Put, url, new JsonObject { ["values"] = values }, token, ct);
    }

    private async Task<JsonNode?> GetAsync(string url, string token, CancellationToken ct) =>
        await SendAsync(HttpMethod.Get, url, null, token, ct);

    private async Task PostAsync(string url, JsonObject body, string token, CancellationToken ct) =>
        await SendAsync(HttpMethod.Post, url, body, token, ct);

    private async Task<JsonNode?> SendAsync(HttpMethod method, string url, JsonObject? body, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Add("Authorization", $"Bearer {token}");
        if (body is not null)
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        var response = await http.SendAsync(request, cts.Token);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync(cts.Token);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    // --- Helpers -------------------------------------------------------------

    /// <summary>A header cell as a stable key: "Created At" and "created_at" both match.</summary>
    private static string Canonical(string title)
    {
        var sb = new StringBuilder();
        foreach (var ch in title.Trim().ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(ch) ? ch : '_');
        return sb.ToString().Trim('_').Replace("__", "_");
    }

    /// <summary>Text that can't be swallowed as a formula when written USER_ENTERED.</summary>
    private static string SafeText(string value) =>
        value.Length > 0 && value[0] is '=' or '+' or '-' or '@' ? "'" + value : value;

    private static int? ParseInt(string s) => int.TryParse(s, out var i) ? i : null;
}
