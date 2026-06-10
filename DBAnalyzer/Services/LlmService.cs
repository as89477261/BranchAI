using System;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace DBAnalyzer.Services
{
    public class LlmService
    {
        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(5)
        };

        public async Task<(string response, string error)> SendMessageAsync(string baseUrl, string aggregatedData, string userPrompt)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(baseUrl))
                    return (string.Empty, "LLM API URL is not configured.");

                var url = baseUrl.TrimEnd('/') + "/v1/chat/completions";

                var userContent = string.IsNullOrWhiteSpace(aggregatedData)
                    ? userPrompt
                    : $"Data:\n{aggregatedData}\n\n{userPrompt}";

                var requestBody = new
                {
                    model = "local-model",
                    messages = new[]
                    {
                        new { role = "system", content = "You are a data analyst." },
                        new { role = "user", content = userContent }
                    },
                    stream = false
                };

                var json = JsonConvert.SerializeObject(requestBody);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = await _httpClient.PostAsync(url, content);
                var responseText = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    return (string.Empty, $"HTTP {(int)response.StatusCode}: {responseText}");
                }

                var jObj = JObject.Parse(responseText);
                var message = jObj["choices"]?[0]?["message"]?["content"]?.ToString();

                if (message == null)
                    return (string.Empty, $"Unexpected response format: {responseText}");

                return (message, string.Empty);
            }
            catch (TaskCanceledException)
            {
                return (string.Empty, "Request timed out. The LLM server may be unavailable.");
            }
            catch (HttpRequestException ex)
            {
                return (string.Empty, $"Network error: {ex.Message}");
            }
            catch (Exception ex)
            {
                return (string.Empty, $"Error: {ex.Message}");
            }
        }
    }
}
