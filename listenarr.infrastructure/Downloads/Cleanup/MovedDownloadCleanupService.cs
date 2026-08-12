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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Listenarr.Infrastructure.Downloads.Cleanup
{
    /// <summary>
    /// Background service that handles moved downloads to remove them from client
    /// Runs every 10 seconds to check for moved downloads
    /// </summary>
    public class MovedDownloadCleanupService(
        IMovedDownloadCleanupProcessor processor,
        ILogger<MovedDownloadCleanupService> logger,
        IWorkerCycleRunner cycleRunner,
        IServiceScopeFactory scopeFactory) : BackgroundService
    {
        private TimeSpan _pollingInterval = TimeSpan.FromSeconds(10);

        public override async Task StartAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("MovedDownloadCleanupService starting");

            try
            {
                using var scope = scopeFactory.CreateScope();
                var configurationService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
                var settings = await configurationService.GetApplicationSettingsAsync();
                if (settings.PollingIntervalSeconds > 0)
                {
                    _pollingInterval = TimeSpan.FromSeconds(settings.PollingIntervalSeconds);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation("MovedDownloadCleanupService startup canceled");
            }
            catch (OperationCanceledException ex)
            {
                logger.LogWarning(ex, "MovedDownloadCleanupService settings load canceled/timed out during startup; using default interval");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "Failed to load polling interval from settings, using default");
            }

            await base.StartAsync(cancellationToken);
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("MovedDownloadCleanupService stopping");
            await base.StopAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("MovedDownloadCleanupService background task started");

            await cycleRunner.RunPeriodicAsync(
                nameof(MovedDownloadCleanupService),
                initialDelay: null,
                intervalProvider: () => _pollingInterval,
                runCycle: processor.RunCycleAsync,
                cancellationToken);

            logger.LogInformation("MovedDownloadCleanupService background task stopped");
        }
    }

    public partial class MovedDownloadCleanupProcessor(
        IServiceScopeFactory scopeFactory,
        ILogger<MovedDownloadCleanupProcessor> logger) : IMovedDownloadCleanupProcessor
    {
        private static readonly TimeSpan ClientRemovalGracePeriod = TimeSpan.FromHours(2);
        private static readonly TimeSpan FailedCleanupWarningGracePeriod = TimeSpan.FromHours(24);
        private static readonly TimeSpan LegacyMovedProofGracePeriod = TimeSpan.FromDays(7);

        private enum ImportProofKind
        {
            None,
            CompletedProcessingJob,
            LastImportedAt,
            ImportedHistory,
            LegacyDownloadHistory,
            LegacyMovedState
        }

        private sealed record ImportProof(
            ImportProofKind Kind,
            string CorrelationId,
            string? ProcessingJobId,
            DateTime? ProvenAt)
        {
            public bool AllowsDestructiveCleanup => Kind is not ImportProofKind.LegacyMovedState;
        }

        /// <summary>
        /// Processes deferred removals for downloads that have been imported (Status == Moved)
        /// but couldn't be removed from the client because the torrent hadn't reached its seed limit.
        /// Checks metadata CanBeRemoved flag which is updated by DownloadMonitorService on each poll.
        /// </summary>
        public async Task RunCycleAsync(CancellationToken cancellationToken)
        {
            using var scope = scopeFactory.CreateScope();
            var downloadRepository = scope.ServiceProvider.GetRequiredService<IDownloadRepository>();
            var configurationService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
            var downloadClientGateway = scope.ServiceProvider.GetRequiredService<IDownloadClientGateway>();
            var processingJobRepository = scope.ServiceProvider.GetRequiredService<IDownloadProcessingJobRepository>();
            var historyRepository = scope.ServiceProvider.GetRequiredService<IHistoryRepository>();
            var downloadHistoryRepository = scope.ServiceProvider.GetRequiredService<IDownloadHistoryRepository>();

            var movedDownloads = (await downloadRepository.GetActiveAsync())
                .Where(d => d.Status == DownloadStatus.Moved)
                .ToList();

            // Pre-load enabled clients once so torrent cross-client cleanup does not reload
            // configuration for every moved download in a cleanup cycle.
            List<DownloadClientConfiguration> allEnabledClients;
            try
            {
                var allClients = await configurationService.GetDownloadClientConfigurationsAsync();
                allEnabledClients = allClients.Where(c => c.IsEnabled).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogDebug(ex, "Failed to load client configurations for deferred removal");
                return;
            }

            // Independent of the Moved lane: dispose of errored client entries whose
            // downloads are terminally blocked/failed and whose files are gone.
            await CleanupBlockedClientEntriesAsync(
                downloadRepository,
                downloadClientGateway,
                historyRepository,
                allEnabledClients,
                cancellationToken);

            if (movedDownloads.Count == 0) return;

            // Move-mode imports relocate the payload out of the client's save
            // path, so the client item can never seed again — qBittorrent flags
            // it "missing files" and it errors forever. There is nothing to
            // wait for: such items are removable as soon as the import is proven,
            // even when the client explicitly reports CanBeRemoved=false.
            var importRelocatesFiles = false;
            try
            {
                var settings = await configurationService.GetApplicationSettingsAsync();
                importRelocatesFiles = settings.CompletedFileAction == Domain.Audiobooks.Enumerations.FileAction.Move;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogDebug(ex, "Failed to load application settings for deferred removal");
            }

            foreach (var download in movedDownloads)
            {
                try
                {
                    var clientConfig = await configurationService.GetDownloadClientConfigurationAsync(download.DownloadClientId);
                    if (clientConfig == null)
                    {
                        logger.LogDebug(
                            "Deferred removal: Skipping download {DownloadId} because client {ClientId} no longer exists",
                            download.Id,
                            download.DownloadClientId);
                        continue;
                    }

                    if (!clientConfig.IsEnabled)
                    {
                        logger.LogDebug("Deferred removal: Skipping download {DownloadId} — client {ClientName} is disabled",
                            download.Id, clientConfig.Name);
                        continue;
                    }

                    var removalPolicy = clientConfig.RemoveCompletedDownloads;
                    if (string.IsNullOrEmpty(removalPolicy) || removalPolicy == "none")
                    {
                        // Policy none is an intentional retention choice, so do not resolve import
                        // proof, infer removability, or write cleanup history for these records.
                        logger.LogDebug("Deferred removal: Retaining imported download {DownloadId}; client action is none", download.Id);
                        continue;
                    }

                    var proof = await ResolveImportProofAsync(
                        download,
                        processingJobRepository,
                        historyRepository,
                        downloadHistoryRepository,
                        cancellationToken);
                    if (proof.Kind == ImportProofKind.None)
                    {
                        logger.LogWarning(
                            "Deferred removal: Download {DownloadId} is Moved without durable import proof; cleanup is blocked",
                            download.Id);
                        continue;
                    }

                    var hasCanBeRemoved = false;
                    var canBeRemoved = false;
                    if (download.Metadata != null && download.Metadata.TryGetValue("CanBeRemoved", out var canRemoveObj))
                    {
                        hasCanBeRemoved = true;
                        canBeRemoved = canRemoveObj is bool b
                            ? b
                            : canRemoveObj is JsonElement je
                                ? je.GetBoolean()
                                : bool.TryParse(canRemoveObj?.ToString(), out var parsed) && parsed;
                    }

                    if (!canBeRemoved && importRelocatesFiles)
                    {
                        // The import moved the files away; seeding is impossible
                        // and the poller will never flip CanBeRemoved.
                        canBeRemoved = true;
                    }

                    // Only infer removability when the client never reported CanBeRemoved. A stored
                    // false value means the client explicitly says cleanup is not ready yet.
                    var timeSinceImportProof = proof.ProvenAt.HasValue
                        ? DateTime.UtcNow - proof.ProvenAt.Value
                        : (TimeSpan?)null;
                    if (!canBeRemoved && !hasCanBeRemoved &&
                        timeSinceImportProof.HasValue &&
                        timeSinceImportProof.Value > ClientRemovalGracePeriod)
                    {
                        logger.LogDebug(
                            "Deferred removal: Download {DownloadId} CanBeRemoved not set after {Hours:F1}h — " +
                            "treating as removable (possible client ID mismatch)", download.Id, timeSinceImportProof.Value.TotalHours);
                        canBeRemoved = true;
                    }

                    if (!canBeRemoved)
                    {
                        logger.LogDebug("Deferred removal: Download {DownloadId} still not removable", download.Id);
                        continue;
                    }

                    var deleteFiles = removalPolicy == "remove_and_delete";
                    if (deleteFiles && !proof.AllowsDestructiveCleanup)
                    {
                        // Legacy Moved alone is enough to clean stale client/DB state, but not
                        // enough to prove it is safe to delete files from the external client.
                        logger.LogWarning(
                            "Deferred removal: Download {DownloadId} has only legacy Moved-state import proof; remove_and_delete was downgraded to remove",
                            download.Id);
                        deleteFiles = false;
                    }

                    if (proof.Kind == ImportProofKind.LegacyMovedState)
                    {
                        logger.LogInformation(
                            "Deferred removal: Attempting non-destructive legacy cleanup for download {DownloadId}",
                            download.Id);
                    }

                    string? torrentHash = null;
                    if (download.Metadata != null && download.Metadata.TryGetValue("TorrentHash", out var hashObj))
                    {
                        torrentHash = hashObj?.ToString();
                    }

                    string? clientDownloadId = null;
                    if (download.Metadata != null && download.Metadata.TryGetValue("ClientDownloadId", out var clientIdObj))
                    {
                        clientDownloadId = clientIdObj?.ToString();
                    }

                    string clientId = !string.IsNullOrEmpty(torrentHash) ? torrentHash
                        : !string.IsNullOrEmpty(clientDownloadId) ? clientDownloadId
                        : download.Id;

                    var removed = false;
                    await AddCleanupHistoryAsync(
                        historyRepository,
                        download,
                        HistoryEvents.CleanupRequested,
                        HistoryOutcome.Requested,
                        proof.CorrelationId,
                        $"Client cleanup requested ({removalPolicy})",
                        BuildCleanupDetails(proof, removalPolicy, deleteFiles),
                        cancellationToken);

                    try
                    {
                        removed = await downloadClientGateway.RemoveAsync(clientConfig, clientId, deleteFiles);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        logger.LogDebug(ex, "Deferred removal: Primary client {ClientName} removal failed for {DownloadId}",
                            clientConfig.Name, download.Id);
                    }

                    // If the primary client did not remove a torrent, try other enabled torrent
                    // clients by hash. This keeps cleanup resilient to older records assigned to
                    // the wrong DownloadClientId.
                    if (!removed && !string.IsNullOrEmpty(torrentHash))
                    {
                        var torrentClientTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                            { "qbittorrent", "transmission" };
                        var otherTorrentClients = allEnabledClients
                            .Where(c => torrentClientTypes.Contains(c.Type ?? "") &&
                                        c.Id != download.DownloadClientId)
                            .ToList();

                        foreach (var altClient in otherTorrentClients)
                        {
                            try
                            {
                                var altDeleteFiles = proof.AllowsDestructiveCleanup &&
                                    altClient.RemoveCompletedDownloads == "remove_and_delete";
                                removed = await downloadClientGateway.RemoveAsync(altClient, torrentHash, altDeleteFiles);
                                if (removed)
                                {
                                    logger.LogInformation(
                                        "Deferred removal: Cross-client removal succeeded — removed {DownloadId} from {ClientName} " +
                                        "(was assigned to {OriginalClientId})",
                                        download.Id, altClient.Name, download.DownloadClientId);
                                    break;
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                            {
                                logger.LogDebug(ex, "Deferred removal: Cross-client removal from {ClientName} failed for {DownloadId}",
                                    altClient.Name, download.Id);
                            }
                        }
                    }

                    if (removed)
                    {
                        logger.LogInformation("Deferred removal: Successfully removed {DownloadId} (deleteFiles={DeleteFiles})",
                            download.Id, deleteFiles);
                        await AddCleanupHistoryAsync(
                            historyRepository,
                            download,
                            HistoryEvents.CleanupSucceeded,
                            HistoryOutcome.Succeeded,
                            proof.CorrelationId,
                            "Client cleanup completed",
                            BuildCleanupDetails(proof, removalPolicy, deleteFiles),
                            cancellationToken);
                        await downloadRepository.RemoveAsync(download.Id);
                    }
                    else if (timeSinceImportProof.HasValue && timeSinceImportProof.Value > FailedCleanupWarningGracePeriod)
                    {
                        logger.LogWarning(
                            "Deferred removal: All removal attempts failed for {DownloadId} after {Hours:F1}h — " +
                            "retaining the operational record for a future retry",
                            download.Id, timeSinceImportProof.Value.TotalHours);
                        var details = BuildCleanupDetails(proof, removalPolicy, deleteFiles);
                        details["OperationalRecordRemoved"] = false;
                        await AddCleanupHistoryAsync(
                            historyRepository,
                            download,
                            HistoryEvents.CleanupFailed,
                            HistoryOutcome.Failed,
                            proof.CorrelationId,
                            "Client cleanup failed after the grace period; operational record retained",
                            details,
                            cancellationToken);
                    }
                    else
                    {
                        logger.LogDebug("Deferred removal: Failed to remove {DownloadId}, will retry next cycle", download.Id);
                        await AddCleanupHistoryAsync(
                            historyRepository,
                            download,
                            HistoryEvents.CleanupFailed,
                            HistoryOutcome.Retrying,
                            proof.CorrelationId,
                            "Client cleanup failed and will be retried",
                            BuildCleanupDetails(proof, removalPolicy, deleteFiles),
                            cancellationToken);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    logger.LogDebug(ex, "Error processing deferred removal for {DownloadId}", download.Id);
                }
            }
        }

    }
}
