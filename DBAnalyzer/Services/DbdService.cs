using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using DBAnalyzer.Models;

namespace DBAnalyzer.Services
{
    public class DbdService
    {
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        // DBD Open Data — ข้อมูลทะเบียนนิติบุคคล
        private const string CkanBase   = "https://opendata.dbd.go.th/api/3/action/datastore_search";
        private const string ResourceId = "bc6ed576-dee1-4b72-b144-e11c8d122e26";

        static DbdService()
        {
            _http.DefaultRequestHeaders.Add("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) DBAnalyzer/2.0");
            _http.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        public async Task<(ContextItem? item, string error)> FetchJuristicAsync(string juristicId)
        {
            if (string.IsNullOrWhiteSpace(juristicId))
                return (null, "กรุณาระบุเลขนิติบุคคล");

            try
            {
                // Try exact field filter first, fallback to full-text search
                var url = $"{CkanBase}?resource_id={ResourceId}" +
                          $"&filters={{\"JuristicPersonID\":\"{juristicId}\"}}&limit=5";

                var json = await _http.GetStringAsync(url);
                var result = ParseCkanResponse(json, juristicId);

                // If no records from field filter, try free-text search
                if (result.item == null && string.IsNullOrEmpty(result.error))
                {
                    var url2 = $"{CkanBase}?resource_id={ResourceId}&q={juristicId}&limit=5";
                    json     = await _http.GetStringAsync(url2);
                    result   = ParseCkanResponse(json, juristicId);
                }

                return result;
            }
            catch (TaskCanceledException)
            {
                return (null, "Request timeout — เซิร์ฟเวอร์ DBD ใช้เวลานาน");
            }
            catch (HttpRequestException ex)
            {
                return (null, $"HTTP Error: {ex.Message}");
            }
            catch (Exception ex)
            {
                return (null, $"Error: {ex.Message}");
            }
        }

        private (ContextItem? item, string error) ParseCkanResponse(string json, string juristicId)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("success", out var successProp) ||
                    !successProp.GetBoolean())
                {
                    var errMsg = root.TryGetProperty("error", out var errProp)
                        ? errProp.ToString()
                        : "API returned success=false";
                    return (null, errMsg);
                }

                var result  = root.GetProperty("result");
                var records = result.GetProperty("records");
                int total   = result.TryGetProperty("total", out var t) ? t.GetInt32() : 0;

                if (records.GetArrayLength() == 0)
                    return (null, string.Empty); // empty — caller will try fallback

                var item = new ContextItem
                {
                    Header = $"DBD Open Data — นิติบุคคล {juristicId}  ({total} record(s))"
                };

                var sb = new StringBuilder();
                sb.AppendLine($"[DBD / นิติบุคคล {juristicId}]");

                foreach (var rec in records.EnumerateArray())
                {
                    var row = new ContextRow();
                    foreach (var prop in rec.EnumerateObject())
                    {
                        if (prop.Name == "_id") continue;
                        var val = prop.Value.ValueKind == JsonValueKind.Null
                            ? "—"
                            : prop.Value.ToString();
                        row.Fields.Add(new ContextField { Key = prop.Name, Value = val });
                        sb.AppendLine($"  {prop.Name}: {val}");
                    }
                    item.Rows.Add(row);
                    sb.AppendLine();
                }

                item.RawText = sb.ToString();
                return (item, string.Empty);
            }
            catch (Exception ex)
            {
                return (null, $"Parse error: {ex.Message}");
            }
        }
    }
}
