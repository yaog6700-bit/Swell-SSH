using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using SwellSSH.Models;

namespace SwellSSH.Services
{
    public class ChatMessage
    {
        public string Role { get; set; } = "user";
        public string Content { get; set; } = "";
    }

    public class AIAssistantService
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        public async IAsyncEnumerable<string> StreamChatAsync(
            ApiEnvironment env,
            List<ChatMessage> messages,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var handler = new HttpClientHandler();

            if (!string.IsNullOrWhiteSpace(env.HttpProxy))
            {
                handler.Proxy = new WebProxy(env.HttpProxy);
                handler.UseProxy = true;
            }

            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(env.MaxRetries > 0 ? 30 : 60) };

            if (!string.IsNullOrWhiteSpace(env.CustomUserAgent))
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd(env.CustomUserAgent);
            }

            string apiKey = ConnectionStorage.DecryptSecret(env.EncryptedApiKey ?? "");
            if (!string.IsNullOrEmpty(apiKey))
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            var requestBody = new Dictionary<string, object>
            {
                { "model", env.CurrentModel },
                { "messages", messages },
                { "stream", true }
            };

            if (!string.IsNullOrWhiteSpace(env.ReasoningEffort))
            {
                requestBody["reasoning_effort"] = env.ReasoningEffort;
            }

            string jsonBody = JsonSerializer.Serialize(requestBody, JsonOptions);
            string baseUrl = env.ApiBaseUrl.TrimEnd('/');
            string url = $"{baseUrl}/chat/completions";

            int maxAttempts = env.MaxRetries > 0 ? env.MaxRetries + 1 : 1;
            HttpResponseMessage? response = null;

            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                try
                {
                    var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
                    using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
                    
                    response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    
                    if (response.IsSuccessStatusCode)
                    {
                        break;
                    }

                    int statusCode = (int)response.StatusCode;
                    if (attempt < maxAttempts && (statusCode == 429 || statusCode >= 500))
                    {
                        var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                        response.Dispose();
                        response = null;
                        await Task.Delay(delay, cancellationToken);
                        continue;
                    }

                    response.EnsureSuccessStatusCode();
                }
                catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException)
                {
                    if (response != null) { response.Dispose(); response = null; }
                    if (attempt >= maxAttempts || cancellationToken.IsCancellationRequested) throw;
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt)), cancellationToken);
                }
            }

            if (response == null) throw new Exception("Failed to send request.");

            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new StreamReader(stream);

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? line = await reader.ReadLineAsync();
                if (line == null) break;

                if (string.IsNullOrWhiteSpace(line)) continue;
                if (!line.StartsWith("data: ")) continue;

                string data = line.Substring(6).Trim();
                if (data == "[DONE]") break;

                JsonDocument doc;
                try
                {
                    doc = JsonDocument.Parse(data);
                }
                catch
                {
                    continue;
                }

                if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var choice = choices[0];
                    if (choice.TryGetProperty("delta", out var delta))
                    {
                        if (delta.TryGetProperty("content", out var contentProp) && contentProp.ValueKind == JsonValueKind.String)
                        {
                            yield return contentProp.GetString() ?? "";
                        }
                    }
                }
            }
        }

        public async Task<bool> TestConnectionAsync(ApiEnvironment env)
        {
            var handler = new HttpClientHandler();
            if (!string.IsNullOrWhiteSpace(env.HttpProxy))
            {
                handler.Proxy = new WebProxy(env.HttpProxy);
                handler.UseProxy = true;
            }
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            if (!string.IsNullOrWhiteSpace(env.CustomUserAgent))
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd(env.CustomUserAgent);
            }
            string apiKey = ConnectionStorage.DecryptSecret(env.EncryptedApiKey ?? "");
            if (!string.IsNullOrEmpty(apiKey))
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            string baseUrl = env.ApiBaseUrl.TrimEnd('/');
            string url = $"{baseUrl}/models"; // 几乎所有兼容 OpenAI 的 API 都支持 /models 端点

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                HttpResponseMessage response = await client.SendAsync(request);
                return response.IsSuccessStatusCode;
            }
            catch
            {
                return false;
            }
        }

        public async Task<List<string>> GetAvailableModelsAsync(ApiEnvironment env)
        {
            var models = new List<string>();
            var handler = new HttpClientHandler();
            if (!string.IsNullOrWhiteSpace(env.HttpProxy))
            {
                handler.Proxy = new WebProxy(env.HttpProxy);
                handler.UseProxy = true;
            }
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
            if (!string.IsNullOrWhiteSpace(env.CustomUserAgent))
            {
                client.DefaultRequestHeaders.UserAgent.ParseAdd(env.CustomUserAgent);
            }
            string apiKey = ConnectionStorage.DecryptSecret(env.EncryptedApiKey ?? "");
            if (!string.IsNullOrEmpty(apiKey))
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            }

            string baseUrl = env.ApiBaseUrl.TrimEnd('/');
            string url = $"{baseUrl}/models";

            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                HttpResponseMessage response = await client.SendAsync(request);
                if (response.IsSuccessStatusCode)
                {
                    string json = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in dataElement.EnumerateArray())
                        {
                            if (item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.String)
                            {
                                string? id = idProp.GetString();
                                if (!string.IsNullOrEmpty(id)) models.Add(id);
                            }
                        }
                    }
                }
            }
            catch { }
            return models;
        }
    }
}
