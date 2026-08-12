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

namespace Listenarr.Infrastructure.Downloads.Cleanup
{
    public partial class MovedDownloadCleanupProcessor
    {
        /// <summary>
        /// Disposes of download-client entries for terminally failed/blocked
        /// downloads whose payload is gone. A missingFiles torrent can never
        /// seed again and its download can never import (it is blocked), so the
        /// client entry only accumulates as a permanent error. The client entry
        /// is removed WITHOUT deleting files (they are already missing) and the
        /// database row is kept as a tombstone so the release stays suppressed.
        /// </summary>
        private async Task CleanupBlockedClientEntriesAsync(
            IDownloadRepository downloadRepository,
            IDownloadClientGateway downloadClientGateway,
            IHistoryRepository historyRepository,
            IReadOnlyList<DownloadClientConfiguration> enabledClients,
            CancellationToken cancellationToken)
        {
            List<Download> blockedDownloads;
            try
            {
                blockedDownloads = (await downloadRepository.GetAllAsync())
                    .Where(d => (d.Status == DownloadStatus.Failed || d.Status == DownloadStatus.ImportBlocked)
                                && !string.IsNullOrEmpty(d.DownloadClientId))
                    .ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogDebug(ex, "Blocked-entry cleanup: failed to load downloads");
                return;
            }

            if (blockedDownloads.Count == 0)
            {
                return;
            }

            foreach (var client in enabledClients)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var clientBlocked = blockedDownloads
                    .Where(d => d.DownloadClientId == client.Id)
                    .ToList();
                if (clientBlocked.Count == 0)
                {
                    continue;
                }

                List<QueueItem> queue;
                try
                {
                    queue = await downloadClientGateway.GetQueueAsync(client, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    logger.LogDebug(ex, "Blocked-entry cleanup: could not fetch queue from client {ClientName}", client.Name);
                    continue;
                }

                var missingFilesItems = queue
                    .Where(item => !string.IsNullOrEmpty(item.Id)
                                   && item.Status == "failed"
                                   && string.Equals(item.ClientFailureReason, "missingFiles", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(item => item.Id!, StringComparer.OrdinalIgnoreCase);
                if (missingFilesItems.Count == 0)
                {
                    continue;
                }

                foreach (var download in clientBlocked)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var clientItemId = download.GetMetadataString("TorrentHash")
                        ?? download.GetMetadataString("ClientDownloadId");
                    if (string.IsNullOrEmpty(clientItemId) || !missingFilesItems.ContainsKey(clientItemId))
                    {
                        continue;
                    }

                    var removed = false;
                    try
                    {
                        removed = await downloadClientGateway.RemoveAsync(client, clientItemId, deleteFiles: false, cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        logger.LogDebug(ex, "Blocked-entry cleanup: removal failed for {DownloadId} on {ClientName}",
                            download.Id, client.Name);
                    }

                    if (!removed)
                    {
                        continue;
                    }

                    logger.LogInformation(
                        "Blocked-entry cleanup: removed missing-files client entry for {Status} download {DownloadId} ('{Title}') from {ClientName}; operational record retained",
                        download.Status, download.Id, LogRedaction.SanitizeText(download.Title), client.Name);

                    await AddCleanupHistoryAsync(
                        historyRepository,
                        download,
                        HistoryEvents.CleanupSucceeded,
                        HistoryOutcome.Succeeded,
                        download.Id.ToUpperInvariant(),
                        "Removed errored client entry (missing files, import blocked); record retained",
                        new Dictionary<string, object>
                        {
                            ["Reason"] = "missingFiles",
                            ["RemovalPolicy"] = "remove",
                            ["DeleteFiles"] = false
                        },
                        cancellationToken);
                }
            }
        }
    }
}
