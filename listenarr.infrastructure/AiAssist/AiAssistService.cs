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
        public const int RequestTimeoutSeconds = 120;

        // Thinking-style models (Qwen3, Qwen3.5, …) reason at length before answering
        // unless told not to; on a CPU/M1 box that turned a 3s verdict into a 2–5 minute
        // one, past the request timeout, so the gate silently failed open. Ollama's
        // OpenAI-compatible endpoint honours reasoning_effort; non-thinking models ignore it.
        public const string ReasoningEffort = "none";
        private const int TestTimeoutSeconds = 30;

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
        };

        private readonly HttpClient _httpClient;
        private readonly IConfigurationService _configurationService;
        private readonly ILogger<AiAssistService> _logger;
        private readonly AiAssistEndpointHealth _health;
        private readonly TimeProvider _timeProvider;

        public AiAssistService(
            HttpClient httpClient,
            IConfigurationService configurationService,
            ILogger<AiAssistService> logger,
            AiAssistEndpointHealth? health = null,
            TimeProvider? timeProvider = null)
        {
            _httpClient = httpClient;
            _configurationService = configurationService;
            _logger = logger;
            _health = health ?? new AiAssistEndpointHealth();
            _timeProvider = timeProvider ?? TimeProvider.System;
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

            // A dead or unreachable endpoint costs the full connect/request timeout per
            // call, and consumers call once per book (release gate, split previews…).
            // Skip for the cooldown instead: the caller keeps its deterministic path now.
            var now = _timeProvider.GetUtcNow();
            if (_health.IsUnavailable(settings.AiAssistBaseUrl, now))
            {
                _logger.LogDebug(
                    "AI assist endpoint {BaseUrl} is in its unavailable cooldown (until {Until:u}); skipping the call",
                    settings.AiAssistBaseUrl, _health.UnavailableUntil(settings.AiAssistBaseUrl, now));
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
                    reasoning_effort = ReasoningEffort,
                    stream = false,
                    messages = new object[]
                    {
                        new { role = "system", content = systemPrompt },
                        new { role = "user", content = userPrompt }
                    }
                });

                var response = await _httpClient.SendAsync(request, linked.Token);
                _health.MarkAvailable();
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
            catch (Exception ex) when (IsTransportFailure(ex))
            {
                // Unreachable host, refused connection, or a timeout (the linked CTS or
                // HttpClient.Timeout — never the caller's token, handled above). Open the
                // cooldown so the next consumers don't each wait the timeout out again.
                var opened = _health.MarkUnavailable(settings.AiAssistBaseUrl, _timeProvider.GetUtcNow());
                if (opened)
                {
                    _logger.LogWarning(ex,
                        "AI assist endpoint {BaseUrl} is unreachable or not answering; skipping AI assist for {Minutes} minutes (consumers keep deterministic behavior)",
                        settings.AiAssistBaseUrl, AiAssistEndpointHealth.DefaultCooldown.TotalMinutes);
                }
                else
                {
                    _logger.LogDebug(ex, "AI assist completion failed again inside the unavailable cooldown");
                }
                return null;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "AI assist completion failed; consumer keeps deterministic behavior");
                return null;
            }
        }

        private static bool IsTransportFailure(Exception ex)
            => ex is HttpRequestException || ex is OperationCanceledException;

        public async Task<AiAssistTestResult> TestConnectionAsync(CancellationToken ct = default)
        {
            var settings = await _configurationService.GetApplicationSettingsAsync();
            return await TestConnectionAsync(settings.AiAssistBaseUrl, settings.AiAssistModel, settings.AiAssistApiKey, ct);
        }

        public async Task<AiAssistTestResult> TestConnectionAsync(string? baseUrl, string? model, string? apiKey, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(model))
            {
                return new AiAssistTestResult(false, "Set a base URL and model first.");
            }

            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(TimeSpan.FromSeconds(TestTimeoutSeconds));

                using var request = BuildChatRequest(baseUrl, apiKey, new
                {
                    model,
                    temperature = 0,
                    reasoning_effort = ReasoningEffort,
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
                var answeredModel = doc.RootElement.TryGetProperty("model", out var m) ? m.GetString() : model;
                // The settings-page probe just proved the endpoint alive: lift any cooldown
                // so the user doesn't have to wait it out after fixing the box.
                _health.MarkAvailable();
                return new AiAssistTestResult(true, $"Connected — model \"{answeredModel}\" answered.");
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
