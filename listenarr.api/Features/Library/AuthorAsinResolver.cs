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

namespace Listenarr.Api.Features.Library
{
    /// <summary>
    /// Resolves author names to Audible author ASINs (and caches the author image),
    /// shared by the add workflow, the update workflow (when the author set changes)
    /// and the author-ASIN audit. Best-effort: a failed lookup contributes nothing.
    /// </summary>
    public sealed class AuthorAsinResolver
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IImageCacheService? _imageCacheService;
        private readonly ILogger<AuthorAsinResolver> _logger;

        public AuthorAsinResolver(
            IServiceScopeFactory scopeFactory,
            ILogger<AuthorAsinResolver> logger,
            IImageCacheService? imageCacheService = null)
        {
            _scopeFactory = scopeFactory;
            _logger = logger;
            _imageCacheService = imageCacheService;
        }

        public async Task<List<string>> ResolveAsync(IEnumerable<string?>? authorNames, CancellationToken ct = default)
        {
            var asins = new List<string>();
            if (authorNames == null)
            {
                return asins;
            }

            using var scope = _scopeFactory.CreateScope();
            var audible = scope.ServiceProvider.GetService<AudibleService>();
            if (audible == null)
            {
                _logger.LogDebug("Audible service unavailable; author ASINs not resolved");
                return asins;
            }

            foreach (var authorName in authorNames)
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(authorName))
                {
                    continue;
                }

                try
                {
                    var info = await audible.LookupAuthorAsync(authorName);
                    if (info == null || string.IsNullOrWhiteSpace(info.Asin))
                    {
                        continue;
                    }

                    if (!asins.Contains(info.Asin))
                    {
                        asins.Add(info.Asin);
                    }

                    await CacheAuthorImageAsync(authorName, info);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Author lookup failed for {Author}", LogRedaction.SanitizeText(authorName));
                }
            }

            return asins;
        }

        private async Task CacheAuthorImageAsync(string authorName, AuthorLookupItem info)
        {
            if (_imageCacheService == null || string.IsNullOrWhiteSpace(info.Asin))
            {
                return;
            }

            try
            {
                var moved = await _imageCacheService.MoveToAuthorLibraryStorageAsync(info.Asin, info.Image);
                if (moved != null)
                {
                    _logger.LogInformation("Cached author image for {Author} (ASIN: {Asin})", LogRedaction.SanitizeText(authorName), info.Asin);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to cache author image for {Author}", LogRedaction.SanitizeText(authorName));
            }
        }
    }
}
