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
using Listenarr.Domain.Downloads;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Downloads
{
    /// <summary>
    /// Best-effort blocklist entry for a release whose download died — keyed
    /// the way the search-time blocklist matches: info-hash first (strongest
    /// identity, from stored metadata, a hash-shaped client item id, or the
    /// magnet link), release title as fallback. Shared by the stall reaper
    /// and the failed-download handler: WITHOUT this, "fail → book still
    /// wanted → re-search → the SAME release scores top again → fail again"
    /// spins forever (live case: three books re-grabbing the same broken
    /// usenet releases every ~4 minutes, 59 download rows each and 24k
    /// client-history entries before anyone noticed). Failure to blocklist
    /// never blocks the caller's own flow.
    /// </summary>
    public static class FailedReleaseBlocklister
    {
        public static async Task BlocklistAsync(
            IBlockedReleaseRepository? repository,
            Download download,
            string reason,
            ILogger logger,
            CancellationToken cancellationToken = default)
        {
            if (repository == null || download.AudiobookId is not > 0 || string.IsNullOrWhiteSpace(download.Title))
            {
                return;
            }

            try
            {
                var hash = ResolveHash(download);

                // The same broken release fails once per monitor cycle until the
                // book grabs something else — blocklist it once, not per cycle.
                var existing = await repository.GetByAudiobookIdAsync(download.AudiobookId.Value, cancellationToken);
                if (Search.BlockedReleaseMatcher.IsBlocked(download.Title, hash, existing))
                {
                    return;
                }

                await repository.AddAsync(new BlockedRelease
                {
                    AudiobookId = download.AudiobookId.Value,
                    ReleaseTitle = download.Title!,
                    TorrentHash = hash,
                    Reason = reason
                }, cancellationToken);

                logger.LogInformation(
                    "Blocklisted failed release for audiobook {AudiobookId}: {Title} ({Reason})",
                    download.AudiobookId, LogRedaction.SanitizeText(download.Title), reason);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "Failed to blocklist failed release for download {DownloadId}", LogRedaction.SanitizeText(download.Id));
            }
        }

        /// <summary>Strongest available identity for the release, or null.</summary>
        internal static string? ResolveHash(Download download)
        {
            var hash = download.GetMetadataString("TorrentHash");
            if (!string.IsNullOrWhiteSpace(hash))
            {
                return hash;
            }

            var externalId = download.GetExternalId();
            if (!string.IsNullOrWhiteSpace(externalId)
                && System.Text.RegularExpressions.Regex.IsMatch(externalId, "^[0-9a-fA-F]{40}$"))
            {
                return externalId;
            }

            if (!string.IsNullOrWhiteSpace(download.OriginalUrl))
            {
                var m = System.Text.RegularExpressions.Regex.Match(download.OriginalUrl, @"btih:([0-9a-fA-F]{40})");
                if (m.Success)
                {
                    return m.Groups[1].Value;
                }
            }

            return null;
        }
    }
}
