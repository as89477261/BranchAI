using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DBAnalyzer.Models;
using Microsoft.Playwright;

namespace DBAnalyzer.Services
{
    /// <summary>
    /// Scrapes DBD DataWarehouse using a real Chromium/Chrome browser via Playwright.
    /// This correctly handles the SPA (Next.js / React) rendering and loads all tabs.
    ///
    /// Requires: Microsoft.Playwright NuGet package.
    /// On first run, call EnsurePlaywrightAsync() which installs browser drivers if needed.
    /// </summary>
    public class PlaywrightScraperService
    {
        private const string BaseUrl  = "https://datawarehouse.dbd.go.th";
        private const int    PageWait = 8000; // ms to wait for SPA content to render

        // Common paths for Chrome on Windows
        private static readonly string[] ChromePaths = new[]
        {
            @"C:\Program Files\Google\Chrome\Application\chrome.exe",
            @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
            Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\Google\Chrome\Application\chrome.exe"),
        };

        // ── Entry point ────────────────────────────────────────────────────────
        public async Task<(List<ContextItem> items, List<string> log)> ScrapeAllTabsAsync(
            string juristicId, Action<string>? onLog = null)
        {
            var log   = new List<string>();
            var items = new List<ContextItem>();
            void Log(string msg) { log.Add(msg); onLog?.Invoke(msg); }

            Log($"▶ Playwright scrape: {BaseUrl}/company/profile/{juristicId}");

            // ── Install Playwright browsers if not present ─────────────────────
            Log("⏳ [1] ตรวจสอบ Playwright browser...");
            try
            {
                var exitCode = Microsoft.Playwright.Program.Main(new[] { "install", "chromium" });
                if (exitCode != 0)
                    Log($"  ⚠ playwright install returned {exitCode} (อาจโอเคถ้ามีอยู่แล้ว)");
                else
                    Log("  ✔ browser พร้อมใช้งาน");
            }
            catch (Exception ex)
            {
                Log($"  ⚠ playwright install: {ex.Message}");
            }

            // ── Launch browser ─────────────────────────────────────────────────
            Log("⏳ [2] เปิด browser...");
            IPlaywright playwright;
            IBrowser     browser;
            try
            {
                playwright = await Playwright.CreateAsync();
                var launchOptions = new BrowserTypeLaunchOptions
                {
                    Headless = true,
                    Args     = new[] { "--no-sandbox", "--disable-setuid-sandbox" },
                };

                // Use system Chrome if available, otherwise fall back to Playwright's Chromium
                var chromePath = ChromePaths.FirstOrDefault(File.Exists);
                if (chromePath != null)
                {
                    Log($"  ✔ ใช้ Chrome: {chromePath}");
                    launchOptions.ExecutablePath = chromePath;
                    browser = await playwright.Chromium.LaunchAsync(launchOptions);
                }
                else
                {
                    Log("  ℹ ไม่พบ Chrome — ใช้ Playwright Chromium");
                    browser = await playwright.Chromium.LaunchAsync(launchOptions);
                }
            }
            catch (Exception ex)
            {
                Log($"❌ เปิด browser ไม่ได้: {ex.Message}");
                Log("  ตรวจสอบว่า Microsoft.Playwright ติดตั้งแล้ว และ playwright install ทำงานสำเร็จ");
                return (items, log);
            }

            try
            {
                var context = await browser.NewContextAsync(new BrowserNewContextOptions
                {
                    UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
                                "AppleWebKit/537.36 (KHTML, like Gecko) " +
                                "Chrome/124.0.0.0 Safari/537.36",
                    Locale    = "th-TH",
                    TimezoneId = "Asia/Bangkok",
                });

                // ── Intercept API responses ────────────────────────────────────
                var capturedApiData = new Dictionary<string, string>(); // url → body
                await context.RouteAsync("**/api/**", async route =>
                {
                    var req = route.Request;
                    await route.ContinueAsync();
                    // We'll capture responses via page.On("response") instead
                });

                var page = await context.NewPageAsync();

                // Capture all JSON API responses
                page.Response += async (_, response) =>
                {
                    try
                    {
                        if (!response.Url.Contains("/api/")) return;
                        var ct = response.Headers.GetValueOrDefault("content-type", "");
                        if (!ct.Contains("json")) return;
                        var body = await response.TextAsync();
                        if (!string.IsNullOrWhiteSpace(body))
                            capturedApiData[response.Url] = body;
                    }
                    catch { }
                };

                // ── Navigate to profile page ───────────────────────────────────
                Log($"⏳ [3] เปิดหน้า /company/profile/{juristicId}...");
                await page.GotoAsync(
                    $"{BaseUrl}/company/profile/{juristicId}",
                    new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 30000 });

                Log($"  ✔ โหลดหน้าสำเร็จ");

                // ── Find all tabs ──────────────────────────────────────────────
                Log("⏳ [4] ค้นหา tab บนหน้า...");
                await page.WaitForTimeoutAsync(2000);

                // Common tab selectors for Thai government Next.js sites
                var tabSelectors = new[]
                {
                    "[role='tab']",
                    ".nav-tabs .nav-link",
                    ".tab-item",
                    "button[data-tab]",
                    ".MuiTab-root",
                    "li[role='tab']",
                };

                IReadOnlyList<ILocator>? tabs = null;
                string? usedSelector = null;
                foreach (var sel in tabSelectors)
                {
                    var found = page.Locator(sel);
                    var count = await found.CountAsync();
                    if (count > 0)
                    {
                        Log($"  ✔ พบ {count} tabs ด้วย selector: {sel}");
                        tabs = Enumerable.Range(0, count)
                                         .Select(i => found.Nth(i))
                                         .ToList();
                        usedSelector = sel;
                        break;
                    }
                }

                if (tabs == null || tabs.Count == 0)
                {
                    Log("  ℹ ไม่พบ tab navigation — จะดึงข้อมูลจากหน้าหลักอย่างเดียว");
                    var singleItem = await ExtractPageDataAsync(page, juristicId, "ข้อมูลนิติบุคคล", Log);
                    if (singleItem != null) items.Add(singleItem);
                }
                else
                {
                    // ── Click each tab and extract data ───────────────────────
                    for (int i = 0; i < tabs.Count; i++)
                    {
                        try
                        {
                            var tabLabel = await tabs[i].InnerTextAsync();
                            tabLabel = tabLabel.Trim().Replace("\n", " ");
                            Log($"⏳ [5.{i + 1}] คลิก tab: \"{tabLabel}\"");

                            await tabs[i].ClickAsync();
                            await page.WaitForLoadStateAsync(LoadState.NetworkIdle,
                                new PageWaitForLoadStateOptions { Timeout = 15000 });
                            await page.WaitForTimeoutAsync(1500);

                            var item = await ExtractPageDataAsync(
                                page, juristicId, tabLabel, Log);
                            if (item != null)
                            {
                                items.Add(item);
                                Log($"  ✔ ดึงข้อมูล tab \"{tabLabel}\" สำเร็จ ({item.Rows.Count} rows)");
                            }
                            else
                            {
                                Log($"  ⚠ tab \"{tabLabel}\" ไม่มีข้อมูล");
                            }
                        }
                        catch (Exception ex)
                        {
                            Log($"  ⚠ tab {i + 1} error: {ex.Message}");
                        }
                    }
                }

                // ── Also parse any captured API responses ──────────────────────
                if (capturedApiData.Count > 0)
                {
                    Log($"⏳ [6] แปลง API responses ที่จับได้ ({capturedApiData.Count} calls)...");
                    foreach (var (url, body) in capturedApiData)
                    {
                        try
                        {
                            var shortUrl = new Uri(url).PathAndQuery;
                            Log($"  API: {shortUrl}");
                            var doc  = JsonDocument.Parse(body);
                            var item = ParseApiJson(doc, juristicId, shortUrl, Log);
                            if (item != null && !items.Any(x => x.Header == item.Header))
                                items.Add(item);
                        }
                        catch { }
                    }
                }

                Log($"");
                if (items.Count > 0)
                    Log($"✅ ดึงข้อมูลสำเร็จ {items.Count} ชุด จาก DBD DataWarehouse");
                else
                    Log($"⚠ ไม่พบข้อมูลใดๆ — ตรวจสอบ ID หรือลองรัน non-headless mode");
            }
            catch (Exception ex)
            {
                Log($"❌ Playwright error: {ex.Message}");
            }
            finally
            {
                await browser.CloseAsync();
            }

            return (items, log);
        }

        // ── Extract structured data from current page state ────────────────────
        private async Task<ContextItem?> ExtractPageDataAsync(
            IPage page, string juristicId, string tabLabel, Action<string> log)
        {
            var item = new ContextItem
            {
                Header = $"DBD DataWarehouse — {tabLabel} ({juristicId})",
                Status = ContextStatus.Loading,
            };
            var sb  = new StringBuilder();
            sb.AppendLine($"[DBD DataWarehouse / {tabLabel} / {juristicId}]");

            try
            {
                // Strategy A: definition lists <dt>/<dd>
                var dts = await page.Locator("dt").AllAsync();
                var rowA = new ContextRow();
                foreach (var dt in dts)
                {
                    try
                    {
                        var key = (await dt.InnerTextAsync()).Trim();
                        // Find the next sibling dd
                        var dd = dt.Locator("xpath=following-sibling::dd[1]");
                        if (await dd.CountAsync() == 0) continue;
                        var val = (await dd.InnerTextAsync()).Trim();
                        if (!string.IsNullOrWhiteSpace(key))
                        {
                            rowA.Fields.Add(new ContextField { Key = key, Value = val });
                            sb.AppendLine($"  {key}: {val}");
                        }
                    }
                    catch { }
                }
                if (rowA.Fields.Count > 0) item.Rows.Add(rowA);

                // Strategy B: label/value pairs (common in React data grids)
                var labelSelectors = new[]
                {
                    ".label, .field-label, .data-label",
                    "[class*='label'], [class*='Label']",
                };
                foreach (var sel in labelSelectors)
                {
                    try
                    {
                        var labels = await page.Locator(sel).AllAsync();
                        if (labels.Count == 0) continue;
                        var rowB = new ContextRow();
                        foreach (var label in labels)
                        {
                            var key = (await label.InnerTextAsync()).Trim();
                            var val = await label.EvaluateAsync<string>(
                                "el => el.nextElementSibling?.innerText ?? el.parentElement?.nextElementSibling?.innerText ?? ''");
                            val = val?.Trim() ?? "";
                            if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(val))
                            {
                                rowB.Fields.Add(new ContextField { Key = key, Value = val });
                                sb.AppendLine($"  {key}: {val}");
                            }
                        }
                        if (rowB.Fields.Count > 0) { item.Rows.Add(rowB); break; }
                    }
                    catch { }
                }

                // Strategy C: tables
                var tables = await page.Locator("table").AllAsync();
                foreach (var table in tables)
                {
                    try
                    {
                        var rows = await table.Locator("tr").AllAsync();
                        var tableRow = new ContextRow();
                        // First pass: check if it's a key/value table (2 columns)
                        bool isKv = true;
                        foreach (var tr in rows.Take(3))
                        {
                            var cells = await tr.Locator("td, th").AllAsync();
                            if (cells.Count != 2) { isKv = false; break; }
                        }

                        if (isKv && rows.Count > 0)
                        {
                            foreach (var tr in rows)
                            {
                                var cells = await tr.Locator("td, th").AllAsync();
                                if (cells.Count < 2) continue;
                                var key = (await cells[0].InnerTextAsync()).Trim();
                                var val = (await cells[1].InnerTextAsync()).Trim();
                                if (!string.IsNullOrWhiteSpace(key))
                                {
                                    tableRow.Fields.Add(new ContextField { Key = key, Value = val });
                                    sb.AppendLine($"  {key}: {val}");
                                }
                            }
                        }
                        else
                        {
                            // Multi-column table: use header as key prefix
                            var headers = new List<string>();
                            var headerRow = await table.Locator("thead tr th, thead tr td").AllAsync();
                            headers.AddRange(await Task.WhenAll(
                                headerRow.Select(async h => (await h.InnerTextAsync()).Trim())));

                            int rowNum = 0;
                            foreach (var tr in rows)
                            {
                                var cells = await tr.Locator("td").AllAsync();
                                if (cells.Count == 0) continue;
                                rowNum++;
                                var dataRow = new ContextRow();
                                for (int ci = 0; ci < cells.Count; ci++)
                                {
                                    var key = headers.Count > ci && !string.IsNullOrWhiteSpace(headers[ci])
                                              ? headers[ci]
                                              : $"Col{ci + 1}";
                                    var val = (await cells[ci].InnerTextAsync()).Trim();
                                    dataRow.Fields.Add(new ContextField { Key = key, Value = val });
                                    sb.AppendLine($"  [{rowNum}] {key}: {val}");
                                }
                                if (dataRow.Fields.Count > 0) item.Rows.Add(dataRow);
                            }
                        }

                        if (tableRow.Fields.Count > 0) item.Rows.Add(tableRow);
                    }
                    catch { }
                }

                // Strategy D: generic text blocks if nothing else found
                if (item.Rows.Count == 0)
                {
                    // Grab main content area text
                    var mainText = await page.EvaluateAsync<string>(
                        @"() => {
                            const main = document.querySelector('main, #main, .main-content, article, .container')
                                         ?? document.body;
                            return main?.innerText ?? '';
                        }");
                    if (!string.IsNullOrWhiteSpace(mainText) && mainText.Length > 50)
                    {
                        var rawRow = new ContextRow();
                        rawRow.Fields.Add(new ContextField
                        {
                            Key   = "raw_text",
                            Value = mainText.Length > 2000
                                    ? mainText[..2000] + "..."
                                    : mainText
                        });
                        item.Rows.Add(rawRow);
                        sb.AppendLine(mainText);
                    }
                }
            }
            catch (Exception ex)
            {
                log($"  ⚠ ExtractPageData error: {ex.Message}");
            }

            if (item.Rows.Count == 0) return null;

            item.RawText = sb.ToString();
            item.Status  = ContextStatus.Success;
            return item;
        }

        // ── Parse captured API JSON ────────────────────────────────────────────
        private ContextItem? ParseApiJson(
            JsonDocument doc, string juristicId, string urlPath, Action<string> log)
        {
            try
            {
                var root = doc.RootElement;
                JsonElement data = root;

                foreach (var w in new[] { "data", "result", "body", "juristic", "company", "financial" })
                    if (root.TryGetProperty(w, out var v) &&
                        (v.ValueKind == JsonValueKind.Object || v.ValueKind == JsonValueKind.Array))
                    { data = v; break; }

                var label  = GuessTabLabel(urlPath);
                var item   = new ContextItem { Header = $"DBD API — {label} ({juristicId})" };
                var sb     = new StringBuilder();
                sb.AppendLine($"[DBD API / {urlPath}]");

                void AddObj(JsonElement el)
                {
                    var row = new ContextRow();
                    foreach (var prop in el.EnumerateObject())
                    {
                        var val = prop.Value.ValueKind == JsonValueKind.Null
                                  ? "—" : prop.Value.ToString();
                        row.Fields.Add(new ContextField { Key = prop.Name, Value = val });
                        sb.AppendLine($"  {prop.Name}: {val}");
                    }
                    if (row.Fields.Count > 0) item.Rows.Add(row);
                }

                if (data.ValueKind == JsonValueKind.Array)
                    foreach (var el in data.EnumerateArray()) AddObj(el);
                else if (data.ValueKind == JsonValueKind.Object)
                    AddObj(data);

                if (item.Rows.Count == 0) return null;
                item.RawText = sb.ToString();
                item.Status  = ContextStatus.Success;
                return item;
            }
            catch { return null; }
        }

        private string GuessTabLabel(string urlPath)
        {
            if (urlPath.Contains("financial") || urlPath.Contains("finance"))  return "งบการเงิน";
            if (urlPath.Contains("shareholder") || urlPath.Contains("holder")) return "ผู้ถือหุ้น";
            if (urlPath.Contains("director") || urlPath.Contains("board"))     return "กรรมการ";
            if (urlPath.Contains("profile") || urlPath.Contains("info"))       return "ข้อมูลทะเบียน";
            return urlPath.Split('/').Last().Split('?').First();
        }

        // ── One-time setup helper ──────────────────────────────────────────────
        /// <summary>Call once at startup to ensure Playwright browsers are installed.</summary>
        public static async Task EnsurePlaywrightAsync()
        {
            await Task.Run(() =>
                Microsoft.Playwright.Program.Main(new[] { "install", "chromium" }));
        }
    }
}
