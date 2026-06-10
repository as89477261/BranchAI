using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using DBAnalyzer.Models;
using HtmlAgilityPack;

namespace DBAnalyzer.Services
{
    /// <summary>
    /// Scrapes DBD DataWarehouse (datawarehouse.dbd.go.th) for juristic person
    /// profile and financial data. Because the site is a Next.js SPA we probe
    /// several likely internal-API patterns and fall back to __NEXT_DATA__ extraction.
    ///
    /// ⚠  This scraper depends on undocumented endpoints — it may break if DBD
    ///    updates their front-end.  All failures are reported via the log callback
    ///    so the caller can display them to the user.
    /// </summary>
    public class DbdScraperService
    {
        // ── HTTP client with browser-like headers ──────────────────────────────
        private static readonly HttpClientHandler _handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
        };

        private static readonly HttpClient _http = new HttpClient(_handler)
        {
            Timeout = TimeSpan.FromSeconds(20),
            BaseAddress = new Uri("https://datawarehouse.dbd.go.th"),
        };

        static DbdScraperService()
        {
            var h = _http.DefaultRequestHeaders;
            h.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                "AppleWebKit/537.36 (KHTML, like Gecko) " +
                "Chrome/124.0.0.0 Safari/537.36");
            h.Add("Accept",
                "text/html,application/xhtml+xml,application/xml;q=0.9," +
                "application/json,*/*;q=0.8");
            h.Add("Accept-Language", "th-TH,th;q=0.9,en-US;q=0.8,en;q=0.7");
            h.Add("Referer", "https://datawarehouse.dbd.go.th/");
        }

        // Action<string> callback — caller displays each log line in the UI
        public async Task<(ContextItem? item, List<string> log)> ScrapeAsync(
            string juristicId, Action<string>? onLog = null)
        {
            var log = new List<string>();
            void Log(string msg) { log.Add(msg); onLog?.Invoke(msg); }

            Log($"▶ เริ่ม scrape DBD DataWarehouse สำหรับ ID: {juristicId}");
            Log($"  Base URL: https://datawarehouse.dbd.go.th");

            // ── Step 1: warm-up request to get session cookie ──────────────────
            Log("⏳ [1/5] ขอ session cookie จาก homepage...");
            try
            {
                var warmup = await _http.GetAsync("/");
                Log($"  ← HTTP {(int)warmup.StatusCode} {warmup.StatusCode}");
            }
            catch (Exception ex)
            {
                Log($"  ⚠ Warm-up failed: {ex.Message}");
                // Continue anyway — server may still respond to API calls
            }

            // ── Step 2: Try known Next.js API patterns ─────────────────────────
            var apiPatterns = new[]
            {
                $"/api/juristic/getInfo?juristicId={juristicId}",
                $"/api/juristic/info?id={juristicId}",
                $"/api/juristic/{juristicId}",
                $"/api/juristic/search?keyword={juristicId}",
                $"/api/getJuristicInfo/{juristicId}",
            };

            foreach (var (pattern, idx) in IndexedPatterns(apiPatterns, 2))
            {
                Log($"⏳ [{idx}/5] GET {pattern}");
                var (json, status, err) = await TryGetJsonAsync(pattern);

                if (err != null)
                {
                    Log($"  ← {status}  ✗ {err}");
                    continue;
                }
                if (json == null)
                {
                    Log($"  ← {status}  (ไม่ใช่ JSON response)");
                    continue;
                }

                Log($"  ← {status}  ✔ ได้ JSON response");
                var item = ParseJsonResponse(json, juristicId, Log);
                if (item != null)
                {
                    Log("✅ ดึงข้อมูลสำเร็จจาก API!");
                    return (item, log);
                }
                Log("  ⚠ parse ข้อมูลไม่ได้ — ลอง pattern ถัดไป");
            }

            // ── Step 3: Fall back — fetch the SPA page and extract __NEXT_DATA__ ─
            Log($"⏳ [5/5] Fallback — scrape HTML page /juristic/{juristicId}...");
            var (html, htmlStatus, htmlErr) = await TryGetHtmlAsync($"/juristic/{juristicId}");

            if (htmlErr != null)
            {
                Log($"  ← {htmlStatus}  ✗ {htmlErr}");
                Log("");
                Log("❌ ไม่สามารถดึงข้อมูลได้");
                Log("สาเหตุที่เป็นไปได้:");
                Log("  • เซิร์ฟเวอร์ block IP นอกไทย (403 Forbidden)");
                Log("  • DBD เปลี่ยน URL pattern แล้ว");
                Log("  • เครือข่ายขัดข้อง");
                Log("ลองเปิด https://datawarehouse.dbd.go.th/juristic แล้วค้นหาเอง");
                return (null, log);
            }

            Log($"  ← {htmlStatus}  ได้ HTML ({html?.Length ?? 0} chars)");
            var fromHtml = ParseNextData(html!, juristicId, Log);
            if (fromHtml != null)
            {
                Log("✅ ดึงข้อมูลจาก __NEXT_DATA__ สำเร็จ!");
                return (fromHtml, log);
            }

            // ── Nothing worked ─────────────────────────────────────────────────
            Log("");
            Log("⚠ ได้ HTML แต่หาข้อมูลนิติบุคคลไม่พบ");
            Log("  อาจเป็นเพราะ:");
            Log("  • เว็บเป็น client-side rendering → ไม่มีข้อมูลใน HTML ตั้งต้น");
            Log("  • เลขนิติบุคคลไม่ถูกต้อง");
            Log("  • DBD อัพเดทโครงสร้าง SPA แล้ว");
            Log("");
            Log("💡 หากต้องการแก้ไข:");
            Log("  1. เปิด Chrome DevTools บน datawarehouse.dbd.go.th");
            Log("  2. กด F12 → Network → XHR/Fetch");
            Log("  3. ค้นหาบริษัท แล้วดู API endpoint ที่ถูกเรียก");
            Log("  4. แจ้ง endpoint URL มาเพื่ออัพเดท scraper");
            return (null, log);
        }

        // ── Financial data (separate endpoint attempt) ─────────────────────────
        public async Task<(ContextItem? item, List<string> log)> ScrapeFinancialAsync(
            string juristicId, Action<string>? onLog = null)
        {
            var log = new List<string>();
            void Log(string msg) { log.Add(msg); onLog?.Invoke(msg); }

            Log($"▶ ดึงงบการเงินจาก DBD DataWarehouse ID: {juristicId}");

            var currentYear = DateTime.Now.Year + 543; // Buddhist era
            var financialPatterns = new[]
            {
                $"/api/juristic/getFinancial?juristicId={juristicId}",
                $"/api/juristic/{juristicId}/financial",
                $"/api/financial?juristicId={juristicId}&year={currentYear}",
                $"/api/financial?juristicId={juristicId}&year={currentYear - 1}",
                $"/api/juristic/financial?id={juristicId}",
            };

            foreach (var (pattern, idx) in IndexedPatterns(financialPatterns, 1))
            {
                Log($"⏳ [{idx}/{financialPatterns.Length}] GET {pattern}");
                var (json, status, err) = await TryGetJsonAsync(pattern);

                if (err != null) { Log($"  ← {status}  ✗ {err}"); continue; }
                if (json == null) { Log($"  ← {status}  (ไม่ใช่ JSON)"); continue; }

                Log($"  ← {status}  ✔ JSON");
                var item = ParseFinancialJson(json, juristicId, Log);
                if (item != null) { Log("✅ ดึงงบการเงินสำเร็จ!"); return (item, log); }
                Log("  ⚠ parse ไม่ได้");
            }

            Log("❌ ไม่พบ endpoint งบการเงิน");
            Log("💡 ใช้วิธี Chrome DevTools เพื่อหา endpoint จริง (ดูคำแนะนำข้างต้น)");
            return (null, log);
        }

        // ── HTTP helpers ───────────────────────────────────────────────────────

        private async Task<(JsonDocument? json, string status, string? error)>
            TryGetJsonAsync(string path)
        {
            try
            {
                var resp = await _http.GetAsync(path);
                var statusStr = $"HTTP {(int)resp.StatusCode}";

                if (!resp.IsSuccessStatusCode)
                    return (null, statusStr, resp.ReasonPhrase);

                var ct = resp.Content.Headers.ContentType?.MediaType ?? "";
                if (!ct.Contains("json"))
                    return (null, statusStr, null); // not JSON

                var body = await resp.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(body))
                    return (null, statusStr, "empty body");

                var doc = JsonDocument.Parse(body);
                return (doc, statusStr, null);
            }
            catch (TaskCanceledException) { return (null, "Timeout", "request timed out"); }
            catch (Exception ex)          { return (null, "Error",   ex.Message); }
        }

        private async Task<(string? html, string status, string? error)>
            TryGetHtmlAsync(string path)
        {
            try
            {
                var resp = await _http.GetAsync(path);
                var statusStr = $"HTTP {(int)resp.StatusCode}";

                if (!resp.IsSuccessStatusCode)
                    return (null, statusStr, resp.ReasonPhrase);

                var body = await resp.Content.ReadAsStringAsync();
                return (body, statusStr, null);
            }
            catch (TaskCanceledException) { return (null, "Timeout", "request timed out"); }
            catch (Exception ex)          { return (null, "Error",   ex.Message); }
        }

        // ── Parsers ────────────────────────────────────────────────────────────

        private ContextItem? ParseJsonResponse(JsonDocument doc, string id, Action<string> log)
        {
            try
            {
                var root = doc.RootElement;

                // Unwrap common wrapper patterns: { data: {...} } or { result: {...} }
                JsonElement data = root;
                foreach (var wrapper in new[] { "data", "result", "body", "juristic", "info" })
                    if (root.TryGetProperty(wrapper, out var w) &&
                        w.ValueKind == JsonValueKind.Object)
                    { data = w; break; }

                if (data.ValueKind != JsonValueKind.Object)
                {
                    log("  ⚠ JSON root ไม่ใช่ object หรือ empty data");
                    return null;
                }

                var item = new ContextItem { Header = $"DBD DataWarehouse — {id}" };
                var sb   = new StringBuilder();
                sb.AppendLine($"[DBD DataWarehouse / {id}]");

                var row = new ContextRow();
                foreach (var prop in data.EnumerateObject())
                {
                    var val = prop.Value.ValueKind == JsonValueKind.Null ? "—"
                            : prop.Value.ValueKind == JsonValueKind.Object ? prop.Value.ToString()
                            : prop.Value.ToString();
                    row.Fields.Add(new ContextField { Key = prop.Name, Value = val });
                    sb.AppendLine($"  {prop.Name}: {val}");
                }
                item.Rows.Add(row);
                item.RawText = sb.ToString();
                item.Header  = ExtractCompanyName(data) is { } name
                    ? $"DBD DataWarehouse — {name} ({id})"
                    : $"DBD DataWarehouse — {id}";
                return item;
            }
            catch (Exception ex) { log($"  ⚠ ParseJson exception: {ex.Message}"); return null; }
        }

        private ContextItem? ParseFinancialJson(JsonDocument doc, string id, Action<string> log)
        {
            try
            {
                var root = doc.RootElement;
                JsonElement data = root;
                foreach (var w in new[] { "data", "result", "financial", "financials" })
                    if (root.TryGetProperty(w, out var v)) { data = v; break; }

                var item = new ContextItem { Header = $"DBD งบการเงิน — {id}" };
                var sb   = new StringBuilder();
                sb.AppendLine($"[DBD งบการเงิน / {id}]");

                void AddRow(JsonElement el)
                {
                    var r = new ContextRow();
                    foreach (var p in el.EnumerateObject())
                    {
                        r.Fields.Add(new ContextField
                        {
                            Key   = p.Name,
                            Value = p.Value.ValueKind == JsonValueKind.Null ? "—" : p.Value.ToString()
                        });
                        sb.AppendLine($"  {p.Name}: {p.Value}");
                    }
                    item.Rows.Add(r);
                    sb.AppendLine();
                }

                if (data.ValueKind == JsonValueKind.Array)
                    foreach (var el in data.EnumerateArray()) AddRow(el);
                else if (data.ValueKind == JsonValueKind.Object)
                    AddRow(data);
                else return null;

                item.RawText = sb.ToString();
                return item.Rows.Count > 0 ? item : null;
            }
            catch (Exception ex) { log($"  ⚠ ParseFinancialJson exception: {ex.Message}"); return null; }
        }

        private ContextItem? ParseNextData(string html, string id, Action<string> log)
        {
            // Next.js embeds server-side props in <script id="__NEXT_DATA__">
            var m = Regex.Match(html,
                @"<script[^>]+id=[""']__NEXT_DATA__[""'][^>]*>([\s\S]*?)</script>",
                RegexOptions.IgnoreCase);

            if (!m.Success)
            {
                log("  ไม่พบ __NEXT_DATA__ — เว็บอาจเป็น pure client-side render");
                return TryParseHtmlDom(html, id, log);
            }

            log("  ✔ พบ __NEXT_DATA__");
            try
            {
                var nextDoc = JsonDocument.Parse(m.Groups[1].Value);
                // Drill down: pageProps → data/juristic/...
                var props = nextDoc.RootElement;
                foreach (var key in new[] { "props", "pageProps" })
                    if (props.TryGetProperty(key, out var v)) props = v;

                return ParseJsonResponse(
                    JsonDocument.Parse(props.ToString()), id, log);
            }
            catch (Exception ex)
            {
                log($"  ⚠ __NEXT_DATA__ parse failed: {ex.Message}");
                return TryParseHtmlDom(html, id, log);
            }
        }

        private ContextItem? TryParseHtmlDom(string html, string id, Action<string> log)
        {
            log("  กำลัง parse HTML DOM ด้วย HtmlAgilityPack...");
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var sb   = new StringBuilder();
            var item = new ContextItem { Header = $"DBD DataWarehouse — {id}" };
            var row  = new ContextRow();

            // Look for definition lists, tables, or data-* attributes
            foreach (var dt in doc.DocumentNode.SelectNodes("//dt") ?? new HtmlNodeCollection(null))
            {
                var dd = dt.SelectSingleNode("following-sibling::dd[1]");
                if (dd == null) continue;
                var key = dt.InnerText.Trim();
                var val = dd.InnerText.Trim();
                row.Fields.Add(new ContextField { Key = key, Value = val });
                sb.AppendLine($"  {key}: {val}");
            }

            foreach (var tr in doc.DocumentNode.SelectNodes("//table//tr") ?? new HtmlNodeCollection(null))
            {
                var cells = tr.SelectNodes("td|th");
                if (cells == null || cells.Count < 2) continue;
                var key = cells[0].InnerText.Trim();
                var val = cells[1].InnerText.Trim();
                if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(val))
                {
                    row.Fields.Add(new ContextField { Key = key, Value = val });
                    sb.AppendLine($"  {key}: {val}");
                }
            }

            if (row.Fields.Count == 0)
            {
                log("  ⚠ ไม่พบข้อมูลใน HTML DOM");
                return null;
            }

            item.Rows.Add(row);
            item.RawText = sb.ToString();
            log($"  ✔ พบ {row.Fields.Count} fields จาก HTML DOM");
            return item;
        }

        private string? ExtractCompanyName(JsonElement el)
        {
            foreach (var key in new[] { "juristicName", "name", "companyName",
                                        "juristic_name", "NameTH", "nameTh", "JuristicName" })
                if (el.TryGetProperty(key, out var v) &&
                    v.ValueKind == JsonValueKind.String)
                    return v.GetString();
            return null;
        }

        private IEnumerable<(T item, int index)> IndexedPatterns<T>(
            T[] items, int startAt)
        {
            for (int i = 0; i < items.Length; i++)
                yield return (items[i], startAt + i);
        }
    }
}
