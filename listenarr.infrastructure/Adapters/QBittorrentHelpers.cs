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
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Adapters
{
    /// <summary>
    /// Shared helper utilities for qBittorrent operations.
    /// Centralizes common functionality to eliminate code duplication across adapters and services.
    /// </summary>
    public static class QBittorrentHelpers
    {
        /// <summary>
        /// Builds a URL parameter string for qBittorrent category filtering.
        /// Extracts the category from client settings and returns a properly formatted parameter.
        /// </summary>
        /// <param name="settings">The client settings dictionary containing optional "category" key</param>
        /// <param name="parameterPrefix">The parameter prefix ("?" for first param, "&amp;" for subsequent params)</param>
        /// <returns>A URL parameter string like "?category=audiobooks" or "&amp;category=audiobooks", or empty string if no category is configured</returns>
        public static string BuildCategoryParameter(Dictionary<string, object> settings, string parameterPrefix)
        {
            if (settings == null || settings.Count == 0)
                return string.Empty;

            if (!settings.TryGetValue("category", out var categoryObj) || categoryObj == null)
                return string.Empty;

            var category = categoryObj.ToString()?.Trim();
            if (string.IsNullOrWhiteSpace(category))
                return string.Empty;

            // Properly encode the category value for URL safety
            var encodedCategory = Uri.EscapeDataString(category);
            return $"{parameterPrefix}category={encodedCategory}";
        }

        /// <summary>
        /// Determines whether a qBittorrent torrent has finished downloading its content.
        /// </summary>
        /// <remarks>
        /// A torrent still fetching its metadata (state "metaDL"/"forcedMetaDL", typically a
        /// dead/0-seeder magnet) reports amount_left == 0 because its total size is not yet
        /// known — NOT because it is complete. Gating the amount_left heuristic on a known
        /// size (size &gt; 0) excludes those false completions, which would otherwise enqueue
        /// an import that finds 0 files and lands the download in ImportBlocked after exhausting
        /// retries. progress &gt;= 1.0 independently catches every genuine completion, so this
        /// can only make completion detection stricter, never miss a real one.
        /// </remarks>
        /// <param name="progress">Torrent progress as a fraction in the range [0, 1].</param>
        /// <param name="amountLeft">Bytes remaining to download (amount_left).</param>
        /// <param name="size">Total size of the selected files in bytes; 0 while metadata is unknown.</param>
        /// <returns>True when the torrent's content is fully downloaded.</returns>
        public static bool IsTorrentComplete(double progress, long amountLeft, long size)
        {
            return progress >= 1.0 || (amountLeft == 0 && size > 0);
        }

        /// <summary>
        /// Logs information about category filtering if a category is configured.
        /// Provides visibility into filtering behavior for debugging and monitoring.
        /// </summary>
        /// <param name="logger">The logger instance to use for logging</param>
        /// <param name="category">The category value to log (can be null or empty)</param>
        public static void LogCategoryFiltering(ILogger logger, string? category)
        {
            if (logger == null)
                return;

            if (string.IsNullOrWhiteSpace(category))
            {
                logger.LogDebug("qBittorrent category filtering not configured - will fetch all torrents");
            }
            else
            {
                logger.LogInformation("Fetching qBittorrent queue filtered by category: {Category}", category);
            }
        }
    }
}
