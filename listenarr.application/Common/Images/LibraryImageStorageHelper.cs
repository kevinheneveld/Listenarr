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

using System.Security.Cryptography;
using System.Text;
using Listenarr.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Common.Images
{
    /// <summary>
    /// Shared helpers for filing audiobook cover art into local library storage.
    /// Both the on-save hook (in LibraryController) and the historical-backlog
    /// sweep service call through here so the cache key derivation and the
    /// external-URL predicate stay in one place.
    /// </summary>
    public static class LibraryImageStorageHelper
    {
        /// <summary>
        /// True iff the URL is an http(s)://… address. The image cache helper returns
        /// relative paths like "/cache/images/library/{key}.jpg" or "/api/v1/images/{key}",
        /// so anything starting with http(s):// is by definition external.
        /// </summary>
        public static bool IsExternalHttpImageUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            return url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Builds the library-storage cache key for an audiobook: prefers ASIN, falls
        /// back to a short hash of the first non-empty ISBN, then to a hash of the
        /// title + first author. Mirrors the keying used by the on-save hook so the
        /// sweep doesn't shard the same record across two different files.
        /// </summary>
        public static string BuildLibraryImageKey(Audiobook audiobook)
        {
            if (audiobook == null) throw new ArgumentNullException(nameof(audiobook));

            if (!string.IsNullOrWhiteSpace(audiobook.Asin))
            {
                return audiobook.Asin!;
            }

            var firstIsbn = audiobook.Isbn?.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i));
            if (!string.IsNullOrWhiteSpace(firstIsbn))
            {
                return "img-" + ComputeShortHash(firstIsbn);
            }

            return "img-" + ComputeShortHash($"{audiobook.Title}|{audiobook.Authors?.FirstOrDefault()}");
        }

        /// <summary>
        /// Downloads an external image into local library storage under the key
        /// derived from <paramref name="audiobook"/> and returns the cache-relative
        /// path (with a leading slash). Returns null when the download fails or the
        /// cache service declines to persist the file.
        /// </summary>
        public static async Task<string?> MoveExternalImageToLibraryAsync(
            IImageCacheService imageCacheService,
            Audiobook audiobook,
            string imageUrl,
            ILogger? logger = null)
        {
            if (imageCacheService == null) throw new ArgumentNullException(nameof(imageCacheService));
            if (audiobook == null) throw new ArgumentNullException(nameof(audiobook));
            if (string.IsNullOrWhiteSpace(imageUrl)) return null;

            try
            {
                var imageKey = BuildLibraryImageKey(audiobook);
                var libraryImagePath = await imageCacheService.MoveToLibraryStorageAsync(imageKey, imageUrl);
                if (string.IsNullOrWhiteSpace(libraryImagePath))
                {
                    return null;
                }

                return "/" + libraryImagePath.TrimStart('/');
            }
            catch (IOException ex)
            {
                logger?.LogWarning(ex, "Failed to move metadata image for audiobook {AudiobookId}", audiobook.Id);
                return null;
            }
            catch (UnauthorizedAccessException ex)
            {
                logger?.LogWarning(ex, "Failed to move metadata image for audiobook {AudiobookId}", audiobook.Id);
                return null;
            }
            catch (HttpRequestException ex)
            {
                logger?.LogWarning(ex, "Failed to move metadata image for audiobook {AudiobookId}", audiobook.Id);
                return null;
            }
            catch (TaskCanceledException ex)
            {
                logger?.LogWarning(ex, "Failed to move metadata image for audiobook {AudiobookId}", audiobook.Id);
                return null;
            }
            catch (InvalidOperationException ex)
            {
                logger?.LogWarning(ex, "Failed to move metadata image for audiobook {AudiobookId}", audiobook.Id);
                return null;
            }
            catch (UriFormatException ex)
            {
                logger?.LogWarning(ex, "Failed to move metadata image for audiobook {AudiobookId}", audiobook.Id);
                return null;
            }
        }

        private static string ComputeShortHash(string? input)
        {
            if (string.IsNullOrEmpty(input))
                return Guid.NewGuid().ToString("N").Substring(0, 12);

            var bytes = Encoding.UTF8.GetBytes(input);
            var hash = SHA1.HashData(bytes);
            return BitConverter.ToString(hash).Replace("-", "").Substring(0, 16).ToLowerInvariant();
        }
    }
}
