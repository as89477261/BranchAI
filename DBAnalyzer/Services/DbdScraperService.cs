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
    /// profile and financial data.
    ///
    /// Profile page URL: /company/profile/{juristicId}
    /// Strategy:
    ///   1. Fetch /company/profile/{id} HTML → extract __NEXT_DATA__ (SSR props)
    ///   2. Use build ID from __NEXT_DATA__ → GET /_next/data/{buildId}/company/profile/{id}.json
    ///   3. Try undocumented REST API patterns derived from the /company/ path
    ///   4. HtmlAgilityPack DOM parse as last resort
    /// </summary>
    public class DbdScraperService
    {
        private static readonly HttpClientHandler _handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            UseCookies = true,
            CookieContainer = new CookieContainer(),
        };

        private static readonly HttpClient _http = new HttpClient(_handler)
        {
            Timeout = TimeSpan.FromSeconds(25),
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

        // ── Public: profile scrape ─────────────────────────────────────────────
        public async Task<(ContextItem? item, List<string> log)> ScrapeAsync(
            string juristicId, Action<string>? onLog = null)
        {
            var log = new List<string>();
            void Log(string msg) { log.Add(msg); onLog?.Invoke(msg); }

            Log($"▶ เริ่ม scrape DBD DataWarehouse ID: {juristicId}");
            Log($"  Profile URL: https://datawarehouse.dbd.go.th/company/profile/{juristicId}");

            // Step 1: fetch the SPA page → get __NEXT_DATA__ + cookies
            Log($"⏳ [1] ดึงหน้า /company/profile/{juristicId}...");
            var (html, htmlStatus, htmlErr) =
                await TryGetHtmlAsync($"/company/profile/{juristicId}");

            if (htmlErr != null)
            {
                Log($"  ← {htmlStatus}  ✗ {htmlErr}");
                Log("");
                Log("❌ ไม่สามารถเข้าถึงเว็บได้");
                Log("สาเหตุที่เป็นไปได้:");
                Log("  • เซิร์ฟเวอร์ block IP นอกไทย (403/timeout)");
                Log("  • ตรวจสอบ network ว่าเข้า datawarehouse.dbd.go.th ได้");
                return (null, log);
            }

            Log($"  ← {htmlStatus}  ได้ HTML {html?.Length ?? 0:N0} chars");

            // Step 2: extract __NEXT_DATA__ (contains SSR data + build ID)
            Log("⏳ [2] ค้นหา __NEXT_DATA__ ใน HTML...");
            string? buildId   = null;
            ContextItem? item = null;

            var nextMatch = Regex.Match(
                html!,
                @"<script[^>]+id=[""']__NEXT_DATA__[""'][^>]*>([\s\S]*?)</script>",
                RegexOptions.IgnoreCase);

            if (nextMatch.Success)
            {
                Log("  ✔ พบ __NEXT_DATA__");
                try
                {
                    var nextDoc  = JsonDocument.Parse(nextMatch.Groups[1].Value);
                    var root     = nextDoc.RootElement;

                    // Extract buildId for /_next/data/ requests
                    if (root.TryGetProperty("buildId", out var bid))
                        buildId = bid.GetString();
                    Log($"  buildId = {buildId ?? "(ไม่พบ)"}");

                    // Try to find data inside pageProps
                    if (root.TryGetProperty("props", out var props) &&
                        props.TryGetProperty("pageProps", out var pageProps))
                    {
                        item = ParseJsonElement(pageProps, juristicId, Log);
                        if (item != null)
                        {
                            Log("✅ ดึงข้อมูลจาก __NEXT_DATA__ → pageProps สำเร็จ!");
                            item.Status = ContextStatus.Success;
                            return (item, log);
                        }
                    }
                    Log("  ⚠ ไม่พบข้อมูลใน pageProps (อาจเป็น client-side fetch)");
                }
                catch (Exception ex)
                {
                    Log($"  ⚠ parse __NEXT_DATA__ ล้มเหลว: {ex.Message}");
                }
            }
            else
            {
                Log("  ไม่พบ __NEXT_DATA__ ใน HTML");
            }

            // Step 3: try /_next/data/{buildId}/company/profile/{id}.json
            if (buildId != null)
            {
                var nextDataPath = $"/_next/data/{buildId}/company/profile/{juristicId}.json";
                Log($"⏳ [3] GET {nextDataPath}");
                var (json, jStatus, jErr) = await TryGetJsonAsync(nextDataPath);
                if (json != null)
                {
                    Log($"  ← {jStatus}  ✔ JSON");
                    item = ParseJsonResponse(json, juristicId, Log);
                    if (item != null)
                    {
                        Log("✅ ดึงข้อมูลจาก _next/data สำเร็จ!");
                        item.Status = ContextStatus.Success;
                        return (item, log);
                    }
                    Log("  ⚠ parse ไม่ได้");
                }
                else
                {
                    Log($"  ← {jStatus}  {jErr ?? "ไม่ใช่ JSON"}");
                }
            }
            else
            {
                Log("⏩ [3] ข้าม — ไม่มี buildId สำหรับ _next/data");
            }

            // Step 4: try REST API patterns based on /company/ path
            var apiPatterns = new[]
            {
                $"/api/company/profile/{juristicId}",
                $"/api/company/{juristicId}",
                $"/api/company/info?juristicId={juristicId}",
                $"/api/juristic/getInfo?juristicId={juristicId}",
                $"/api/juristic/{juristicId}",
            };

            for (int i = 0; i < apiPatterns.Length; i++)
            {
                var path = apiPatterns[i];
                Log($"⏳ [4.{i + 1}] GET {path}");
                var (json, jStatus, jErr) = await TryGetJsonAsync(path);
                if (json == null) { Log($"  ← {jStatus}  {jErr ?? "ไม่ใช่ JSON"}"); continue; }

                Log($"  ← {jStatus}  ✔ JSON");
                item = ParseJsonResponse(json, juristicId, Log);
                if (item != null)
                {
                    Log("✅ ดึงข้อมูลจาก REST API สำเร็จ!");
                    item.Status = ContextStatus.Success;
                    return (item, log);
                }
                Log("  ⚠ parse ไม่ได้");
            }

            // Step 5: DOM parse from the HTML we already have
            Log("⏳ [5] Fallback — parse HTML DOM...");
            item = TryParseHtmlDom(html!, juristicId, Log);
            if (item != null)
            {
                Log("✅ ดึงข้อมูลจาก HTML DOM สำเร็จ!");
                item.Status = ContextStatus.Success;
                return (item, log);
            }

            // All failed
            Log("");
            Log("❌ ดึงข้อมูลไม่สำเร็จ — เว็บน่าจะ render ฝั่ง client ล้วน");
            Log("💡 วิธีแก้:");
            Log("  1. เปิด Chrome DevTools บน datawarehouse.dbd.go.th/company/profile/" + juristicId);
            Log("  2. กด F12 → Network tab → กรอง XHR/Fetch");
            Log("  3. ดู request ที่ถูกยิงออกไป แล้วส่ง API endpoint มาให้เพื่ออัพเดท scraper");
            return (null, log);
        }

        // ── Public: financial scrape ───────────────────────────────────────────
        public async Task<(ContextItem? item, List<string> log)> ScrapeFinancialAsync(
            string juristicId, Action<string>? onLog = null)
        {
            var log = new List<string>();
            void Log(string msg) { log.Add(msg); onLog?.Invoke(msg); }

            Log($"▶ ดึงงบการเงิน DBD DataWarehouse ID: {juristicId}");

            // First get buildId from the profile page (may already be cached by cookie)
            var (html, _, _) = await TryGetHtmlAsync($"/company/profile/{juristicId}");
            string? buildId = null;
            if (html != null)
            {
                var m = Regex.Match(html,
                    @"""buildId""\s*:\s*""([^""]+)""",
                    RegexOptions.IgnoreCase);
                if (m.Success) buildId = m.Groups[1].Value;
                Log($"  buildId = {buildId ?? "(ไม่พบ)"}");
            }

            var currentYear = DateTime.Now.Year + 543;
            var patterns = new List<string>();

            // _next/data patterns for financial sub-pages
            if (buildId != null)
            {
                patterns.Add($"/_next/data/{buildId}/company/financial/{juristicId}.json");
                patterns.Add($"/_next/data/{buildId}/company/profile/{juristicId}.json?tab=financial");
            }

            patterns.AddRange(new[]
            {
                $"/api/company/financial/{juristicId}",
                $"/api/company/{juristicId}/financial",
                $"/api/company/financial?juristicId={juristicId}&year={currentYear}",
                $"/api/company/financial?juristicId={juristicId}&year={currentYear - 1}",
                $"/api/juristic/getFinancial?juristicId={juristicId}",
            });

            for (int i = 0; i < patterns.Count; i++)
            {
                var path = patterns[i];
                Log($"⏳ [{i + 1}/{patterns.Count}] GET {path}");
                var (json, status, err) = await TryGetJsonAsync(path);
                if (json == null) { Log($"  ← {status}  {err ?? "ไม่ใช่ JSON"}"); continue; }

                Log($"  ← {status}  ✔ JSON");
                var item = ParseFinancialJson(json, juristicId, Log);
                if (item != null)
                {
                    Log("✅ ดึงงบการเงินสำเร็จ!");
                    item.Status = ContextStatus.Success;
                    return (item, log);
                }
                Log("  ⚠ parse ไม่ได้");
            }

            Log("❌ ไม่พบ endpoint งบการเงิน");
            Log("💡 ตรวจสอบ Network tab ใน Chrome DevTools ที่หน้า /company/profile/" + juristicId + " แล้วเปิด tab งบการเงิน");
            return (null, log);
        }

        // ── HTTP helpers ───────────────────────────────────────────────────────

        private async Task<(JsonDocument? json, string status, string? error)>
            TryGetJsonAsync(string path)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, path);
                req.Headers.Add("Accept", "application/json, */*");
                var resp = await _http.SendAsync(req);
                var statusStr = $"HTTP {(int)resp.StatusCode}";

                if (!resp.IsSuccessStatusCode)
                    return (null, statusStr, resp.ReasonPhrase);

                var body = await resp.Content.ReadAsStringAsync();
                if (string.IsNullOrWhiteSpace(body))
                    return (null, statusStr, "empty body");

                var ct = resp.Content.Headers.ContentType?.MediaType ?? "";
                // Accept JSON even if content-type is text/plain (some APIs do this)
                if (!ct.Contains("json") && !body.TrimStart().StartsWith("{") && !body.TrimStart().StartsWith("["))
                    return (null, statusStr, null);

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
            try { return ParseJsonElement(doc.RootElement, id, log); }
            catch (Exception ex) { log($"  ⚠ ParseJson exception: {ex.Message}"); return null; }
        }

        private ContextItem? ParseJsonElement(JsonElement root, string id, Action<string> log)
        {
            // Unwrap common wrapper patterns
            JsonElement data = root;
            foreach (var wrapper in new[] { "data", "result", "body", "pageProps",
                                            "juristic", "info", "company", "profile" })
            {
                if (root.TryGetProperty(wrapper, out var w) &&
                    w.ValueKind == JsonValueKind.Object)
                { data = w; break; }
            }

            if (data.ValueKind != JsonValueKind.Object)
            {
                log("  ⚠ JSON ไม่ใช่ object หรือ empty");
                return null;
            }

            // Check there's at least some meaningful content
            bool hasContent = false;
            foreach (var _ in data.EnumerateObject()) { hasContent = true; break; }
            if (!hasContent) return null;

            var item = new ContextItem { Header = $"DBD DataWarehouse — {id}" };
            var sb   = new StringBuilder();
            sb.AppendLine($"[DBD DataWarehouse / {id}]");

            var row = new ContextRow();
            foreach (var prop in data.EnumerateObject())
            {
                var val = prop.Value.ValueKind switch
                {
                    JsonValueKind.Null   => "—",
                    JsonValueKind.Object => prop.Value.ToString(),
                    JsonValueKind.Array  => prop.Value.ToString(),
                    _                    => prop.Value.ToString()
                };
                row.Fields.Add(new ContextField { Key = prop.Name, Value = val });
                sb.AppendLine($"  {prop.Name}: {val}");
            }

            if (row.Fields.Count == 0) return null;

            item.Rows.Add(row);
            item.RawText = sb.ToString();
            item.Header  = ExtractCompanyName(data) is { } name
                ? $"DBD DataWarehouse — {name} ({id})"
                : $"DBD DataWarehouse — {id}";
            return item;
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
            catch (Exception ex) { log($"  ⚠ ParseFinancialJson: {ex.Message}"); return null; }
        }

        private ContextItem? TryParseHtmlDom(string html, string id, Action<string> log)
        {
            log("  parse HTML DOM ด้วย HtmlAgilityPack...");
            var doc  = new HtmlDocument();
            doc.LoadHtml(html);

            var sb   = new StringBuilder();
            var item = new ContextItem { Header = $"DBD DataWarehouse — {id}" };
            var row  = new ContextRow();

            foreach (var dt in doc.DocumentNode.SelectNodes("//dt") ?? new HtmlNodeCollection(null))
            {
                var dd = dt.SelectSingleNode("following-sibling::dd[1]");
                if (dd == null) continue;
                var key = dt.InnerText.Trim();
                var val = dd.InnerText.Trim();
                if (!string.IsNullOrWhiteSpace(key))
                {
                    row.Fields.Add(new ContextField { Key = key, Value = val });
                    sb.AppendLine($"  {key}: {val}");
                }
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
                log("  ⚠ ไม่พบข้อมูลใน HTML DOM (เว็บเป็น CSR ล้วน)");
                return null;
            }

            item.Rows.Add(row);
            item.RawText = sb.ToString();
            log($"  ✔ พบ {row.Fields.Count} fields จาก HTML DOM");
            return item;
        }

        private string? ExtractCompanyName(JsonElement el)
        {
            foreach (var key in new[] {
                "juristicName", "name", "companyName", "juristic_name",
                "NameTH", "nameTh", "JuristicName", "titleName", "title" })
            {
                if (el.TryGetProperty(key, out var v) &&
                    v.ValueKind == JsonValueKind.String)
                    return v.GetString();
            }
            return null;
        }
    }
}
