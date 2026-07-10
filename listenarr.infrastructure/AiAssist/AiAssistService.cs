/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using System.Text;
using System.Text.Json;
using Listenarr.Application.Configuration.Contracts;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.AiAssist
{
    /// <summary>
    /// OpenAI-compatible chat-completions client for the optional AI-assist
    /// endpoint. Settings are read per call (no restart needed after edits).
    /// Every failure mode — disabled, bad URL, endpoint asleep, timeout,
    /// non-2xx, unparsable body — degrades to null so consumers keep their
    /// deterministic behavior; per this week's Audnexus lesson, the request
    /// is bounded by a locally-scoped CancellationTokenSource and
    /// TaskCanceledException is caught here, never propagated.
    /// </summary>
    public class AiAssistService : IAiAssistService
    {
        // Local model servers generate slowly (CPU Macs, small GPUs); a split
        // preview asking for a few hundred output tokens can legitimately take
        // over a minute on an 8B model. Still bounded — never the 100s default
        // on top of unbounded generation.
        private const int RequestTimeoutSeconds = 120;
        private const int TestTimeoutSeconds = 30;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };

        private readonly HttpClient _httpClient;
        private readonly IConfigurationService _configurationService;
        private readonly ILogger<AiAssistService> _logger;

        public AiAssistService(
            HttpClient httpClient,
            IConfigurationService configurationService,
            ILogger<AiAssistService> logger)
        {
            _httpClient = httpClient;
            _configurationService = configurationService;
            _logger = logger;
        }

        public async Task<bool> IsConfiguredAsync(CancellationToken ct = default)
        {
            var settings = await _configurationService.GetApplicationSettingsAsync();
            return settings.AiAssistEnabled
                && !string.IsNullOrWhiteSpace(settings.AiAssistBaseUrl)
                && !string.IsNullOrWhiteSpace(settings.AiAssistModel);
        }

        public async Task<string?> CompleteJsonAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
        {
            var settings = await _configurationService.GetApplicationSettingsAsync();
            if (!settings.AiAssistEnabled
                || string.IsNullOrWhiteSpace(settings.AiAssistBaseUrl)
                || string.IsNullOrWhiteSpace(settings.AiAssistModel))
            {
                return null;
            }

            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(TimeSpan.FromSeconds(RequestTimeoutSeconds));

                using var request = BuildChatRequest(settings.AiAssistBaseUrl, settings.AiAssistApiKey, new
                {
                    model = settings.AiAssistModel,
                    temperature = 0,
                    stream = false,
                    messages = new object[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userPrompt }
                    }
                });

                var response = await _httpClient.SendAsync(request, linked.Token);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("AI assist endpoint returned {StatusCode}", response.StatusCode);
                    return null;
                }

                var body = await response.Content.ReadAsStringAsync(linked.Token);
                using var doc = JsonDocument.Parse(body);
                var content = doc.RootElement
                    .GetProperty("choices")[0]
                    .GetProperty("message")
                    .GetProperty("content")
                    .GetString();
                return string.IsNullOrWhiteSpace(content) ? null : content;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // caller cancelled — propagate normally
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                // Timeouts land here too (the linked CTS, not the caller's token).
                _logger.LogWarning(ex, "AI assist completion failed; consumer keeps deterministic behavior");
                return null;
            }
        }

        public async Task<AiAssistTestResult> TestConnectionAsync(CancellationToken ct = default)
        {
            var settings = await _configurationService.GetApplicationSettingsAsync();
            if (string.IsNullOrWhiteSpace(settings.AiAssistBaseUrl) || string.IsNullOrWhiteSpace(settings.AiAssistModel))
            {
                return new AiAssistTestResult(false, "Set a base URL and model first.");
            }

            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(TimeSpan.FromSeconds(TestTimeoutSeconds));

                using var request = BuildChatRequest(settings.AiAssistBaseUrl, settings.AiAssistApiKey, new
                {
                    model = settings.AiAssistModel,
                    temperature = 0,
                    stream = false,
                    max_tokens = 20,
                    messages = new object[]
                    {
                        new { role = "user", content = "Reply with the single word: ok" }
                    }
                });

                var response = await _httpClient.SendAsync(request, linked.Token);
                var body = await response.Content.ReadAsStringAsync(linked.Token);
                if (!response.IsSuccessStatusCode)
                {
                    var detail = body.Length > 300 ? body[..300] : body;
                    return new AiAssistTestResult(false, $"HTTP {(int)response.StatusCode}: {detail}");
                }

                using var doc = JsonDocument.Parse(body);
                var model = doc.RootElement.TryGetProperty("model", out var m) ? m.GetString() : settings.AiAssistModel;
                return new AiAssistTestResult(true, $"Connected — model \"{model}\" answered.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                return new AiAssistTestResult(false, ex.Message);
            }
        }

        private static HttpRequestMessage BuildChatRequest(string baseUrl, string? apiKey, object payload)
        {
            // Accept a base URL with or without the /v1 suffix: Ollama exposes
            // http://host:11434/v1/chat/completions; users paste both forms.
            var root = baseUrl.TrimEnd('/');
            if (!root.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                root += "/v1";
            }

            var request = new HttpRequestMessage(HttpMethod.Post, $"{root}/chat/completions")
            {
                Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json")
            };
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {apiKey}");
            }
            return request;
        }
    }
}
