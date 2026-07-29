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

namespace Listenarr.Infrastructure.DownloadClients.Qbittorrent
{
    /// <summary>
    /// Shared helper utilities for qBittorrent operations.
    /// Centralizes common functionality to eliminate code duplication across adapters and services.
    /// </summary>
    public static class QBittorrentHelpers
    {
        // Client states in which AmountLeft is meaningless: the torrent has no
        // (verified) content yet, so "0 bytes left" describes ignorance, not
        // completion.
        private static readonly HashSet<string> PreContentStates = new(StringComparer.OrdinalIgnoreCase)
        {
            "metaDL",             // fetching metadata from the swarm — size unknown
            "forcedMetaDL",
            "allocating",
            "checkingDL",
            "checkingUP",
            "checkingResumeData",
        };

        /// <summary>
        /// True when a torrent's reported numbers actually mean "download
        /// finished". A torrent still fetching its metadata reports
        /// AmountLeft == 0 because the client doesn't know the size yet —
        /// treating that as completion marked dead magnets Complete at 0%
        /// (live case: import jobs ground forever against an empty content
        /// path, and cleaning the client entry re-triggered a search that
        /// re-grabbed the same dead magnet). Real completion requires actual
        /// content (Size &gt; 0) in a post-metadata state. Pure for unit
        /// testing.
        /// </summary>
        public static bool IsCompleteCandidate(double progress, long amountLeft, long size, string? state)
        {
            if (size <= 0) return false;
            if (!string.IsNullOrWhiteSpace(state) && PreContentStates.Contains(state.Trim())) return false;
            return progress >= 1.0 || amountLeft == 0;
        }

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
