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
using Listenarr.Application.Metadata.Amazon;
using Listenarr.Application.Metadata.Audible;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Metadata.Providers.Amazon
{
    /// <summary>
    /// Fetches an Amazon dp page and hands it to
    /// <see cref="AmazonProductPageParser"/>. Best-effort by contract: bot
    /// walls, non-200s, timeouts, and unparseable pages all yield null.
    /// Browser-shaped headers matter — Amazon serves stripped or challenge
    /// pages to obvious non-browsers. Residential IPs (the self-host norm)
    /// generally pass; datacenter IPs may see captchas, which surface here
    /// as a logged null, never an error.
    /// </summary>
    public sealed class AmazonProductMetadataService : IAmazonProductMetadataService
    {
        private const string BrowserUserAgent =
            "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

        private readonly HttpClient _httpClient;
        private readonly ILogger<AmazonProductMetadataService> _logger;

        public AmazonProductMetadataService(HttpClient httpClient, ILogger<AmazonProductMetadataService> logger)
        {
            _httpClient = httpClient;
            _logger = logger;
        }

        public async Task<AudibleBookResponse?> GetBookMetadataAsync(string asin, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(asin)) return null;

            var url = $"https://www.amazon.com/dp/{Uri.EscapeDataString(asin.Trim())}";
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", BrowserUserAgent);
                request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
                request.Headers.TryAddWithoutValidation("Accept-Language", "en-US,en;q=0.9");

                using var response = await _httpClient.SendAsync(request, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("Amazon page for {Asin} returned {Status}", asin, (int)response.StatusCode);
                    return null;
                }

                var html = await response.Content.ReadAsStringAsync(cancellationToken);
                if (LooksLikeRobotWall(html))
                {
                    _logger.LogWarning("Amazon served a robot check for {Asin}; skipping Amazon metadata", asin);
                    return null;
                }

                var parsed = AmazonProductPageParser.Parse(html, asin.Trim().ToUpperInvariant());
                if (parsed == null)
                {
                    _logger.LogInformation("Amazon page for {Asin} did not parse as a product page", asin);
                }
                else
                {
                    _logger.LogInformation(
                        "Amazon metadata for {Asin}: '{Title}' ({Minutes} min, narrated by {Narrators})",
                        asin, parsed.Title, parsed.LengthMinutes,
                        string.Join(", ", (parsed.Narrators ?? new List<AudibleNarrator>()).Select(n => n.Name)));
                }
                return parsed;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Amazon metadata fetch failed for {Asin}", asin);
                return null;
            }
        }

        private static bool LooksLikeRobotWall(string? html)
        {
            if (string.IsNullOrWhiteSpace(html)) return true;
            // Challenge pages are tiny relative to real dp pages and carry
            // distinctive phrases; a real product page is hundreds of KB.
            return html.Length < 20_000
                || html.Contains("api-services-support@amazon.com", StringComparison.OrdinalIgnoreCase)
                || html.Contains("To discuss automated access to Amazon data", StringComparison.OrdinalIgnoreCase)
                || html.Contains("Robot Check", StringComparison.OrdinalIgnoreCase);
        }
    }
}
