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

using Listenarr.Application.Interfaces;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Application.Security;
using Listenarr.Domain.Models;
using Listenarr.Domain.Models.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Downloads
{
    /// <summary>
    /// Background service that monitors downloads
    /// - Uses DownloadClientGateway to fetch download updates
    /// - Persists updates
    /// - Handles retry timings on polling failures
    /// CompletedDownloadProcessor handles downloads in completed status
    /// </summary>
    public class DownloadMonitorService(
        IServiceScopeFactory scopeFactory,
        IDownloadPushService downloadPushService,
        ILogger<DownloadMonitorService> logger) : BackgroundService
    {
        private int _pollingInterval = 30;
        private DateTime _lastFullBroadcast = DateTime.MinValue;

        // Per-client polling controls to avoid overloading download clients
        internal readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _nextClientPoll = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> _clientFailureCounts = new();

        // Stalled-download reaper: remembers the last observed progress (and when it was observed)
        // per download Id so we can detect downloads that make no progress for a configured timeout.
        // In-memory (like _nextClientPoll): a restart simply restarts each download's stall window.
        internal readonly System.Collections.Concurrent.ConcurrentDictionary<string, (decimal Progress, DateTime At)> _stallSnapshots = new();

        internal void ScheduleNextClientPoll(DownloadClientConfiguration client, double interval)
        {
            // Add small jitter to avoid synchronized polls: +/- 5s
            var jitter = (int)(new Random().NextDouble() * 10 - 5);
            var next = DateTime.UtcNow.AddSeconds(interval + jitter);

            _nextClientPoll.AddOrUpdate(client.Id, next, (_, __) => next);
            logger.LogDebug($"Scheduled next poll for client {client.Id} at {next} (interval {interval})");
        }

        /// <summary>
        /// Schedule next poll for a client after a successful interaction
        /// </summary>
        private void ScheduleNextClientPollOnSuccess(DownloadClientConfiguration client)
        {
            // Reset failure count
            _clientFailureCounts.TryRemove(client.Id, out _);
            ScheduleNextClientPoll(client, client.GetPollingInterval(_pollingInterval));
        }

        /// <summary>
        /// Schedule next poll for a client after a failure using exponential backoff
        /// </summary>
        private void ScheduleNextClientPollOnFailure(DownloadClientConfiguration client)
        {
            var count = _clientFailureCounts.AddOrUpdate(client.Id, 1, (_, old) => old + 1);
            ScheduleNextClientPoll(client, Math.Min(900, 30 * Math.Pow(2, count - 1)));
        }

        protected override async Task ExecuteAsync(CancellationToken cancellationToken)
        {
            logger.LogInformation("Download Monitor Service starting");

            // Wait a bit before starting to ensure the app is fully initialized
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);

            // Attempt to read configured polling interval from ApplicationSettings (fallback to current default)
            using var scope = scopeFactory.CreateScope();
            var configurationService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
            var appSettings = await configurationService.GetApplicationSettingsAsync();
            if (appSettings.PollingIntervalSeconds > 0)
            {
                _pollingInterval = appSettings.PollingIntervalSeconds;
            }
            logger.LogInformation($"DownloadMonitorService polling interval set to {_pollingInterval}s");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await MonitorDownloadsAsync(cancellationToken);

                    await Task.Delay(TimeSpan.FromSeconds(_pollingInterval), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    // Those exceptions are expected, service should stop gracefully
                }
            }

            logger.LogInformation("Download Monitor Service stopping");
        }

        internal async Task MonitorDownloadsAsync(CancellationToken cancellationToken)
        {
            using var scope = scopeFactory.CreateScope();
            var downloadRepository = scope.ServiceProvider.GetRequiredService<IDownloadRepository>();
            var configurationService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
            var downloadClientGateway = scope.ServiceProvider.GetRequiredService<IDownloadClientGateway>();

            var appSettings = await configurationService.GetApplicationSettingsAsync();

            var configuredClients = await configurationService.GetDownloadClientConfigurationsAsync();
            HashSet<string> enabledClientIds = configuredClients
                .Where(c => c.IsEnabled && !string.IsNullOrWhiteSpace(c.Id))
                .Select(c => c.Id)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (enabledClientIds.Count == 0)
            {
                logger.LogInformation("No enabled download clients configured; skipping client polling");
                return;
            }

            var activeDownloads = await downloadRepository.GetActiveAsync();

            logger.LogInformation("DownloadMonitorService found {Count} active downloads", activeDownloads.Count);
            foreach (var dl in activeDownloads)
            {
                logger.LogDebug("Active download: {Id} - {Title} - Status: {Status} - Client: {ClientId}",
                    LogRedaction.SanitizeText(dl.Id), LogRedaction.SanitizeText(dl.Title), dl.Status, LogRedaction.SanitizeText(dl.DownloadClientId));
            }

            // Filter download with active client configuration
            activeDownloads = [.. activeDownloads.Where(d => enabledClientIds.Contains(d.DownloadClientId))];
            if (activeDownloads.Count <= 0)
            {
                logger.LogInformation("No active downloads mapped to enabled download clients; skipping client polling");
                return;
            }

            var reaperOptions = StalledReaperOptions.FromEnvironment();
            // Drop stall snapshots for downloads that are no longer active (imported, removed, etc.)
            // so the in-memory store can't grow unbounded.
            if (!_stallSnapshots.IsEmpty)
            {
                var activeIds = activeDownloads.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var staleId in _stallSnapshots.Keys.Where(k => !activeIds.Contains(k)).ToList())
                {
                    _stallSnapshots.TryRemove(staleId, out _);
                }
            }

            // Lets assume downloads have been updated by now
            if (DateTime.UtcNow - _lastFullBroadcast > TimeSpan.FromSeconds(120))
            {
                await OnDownloadsUpdated(activeDownloads, cancellationToken);
                _lastFullBroadcast = DateTime.UtcNow;
            }

            logger.LogInformation($"Calling PollDownloadClientsAsync with {activeDownloads.Count} downloads");

            // Group downloads by client
            var downloadsByClient = activeDownloads
                .GroupBy(d => d.DownloadClientId);

            foreach (var group in downloadsByClient)
            {
                // Will always succeed as long as downloads are filtered on active clients
                var client = configuredClients.FirstOrDefault(c => c.Id == group.Key);
                if (client == null)
                {
                    continue;
                }

                // Respect per-client poll schedules to avoid overloading qbittorrent
                if (_nextClientPoll.TryGetValue(client.Id, out var scheduled) && DateTime.UtcNow < scheduled)
                {
                    logger.LogDebug($"Skipping qBittorrent poll for client {client.Id}, next scheduled at {scheduled}");
                    continue;
                }

                var clientDownloads = group.ToList();
                logger.LogInformation($"Processing client group: ClientId={client.Id}, Count={clientDownloads.Count}");

                try
                {
                    var previousDownloads = clientDownloads.Select(item => item.Clone()).ToList();
                    var updatedDownloads = await downloadClientGateway.FetchDownloadsAsync(client, clientDownloads, cancellationToken);

                    var reapCandidates = new List<Download>();
                    var now = DateTime.UtcNow;

                    foreach (Download download in updatedDownloads)
                    {
                        var downloadService = scope.ServiceProvider.GetRequiredService<IDownloadService>();
                        await downloadService.UpdateAsync(download);

                        // Stall reaper: update this download's progress snapshot and flag it if it has
                        // made no progress past the timeout. Detection runs whenever the reaper is
                        // enabled (dry-run included); only the removal action below is gated on dry-run.
                        if (reaperOptions.Enabled)
                        {
                            EvaluateStallSnapshot(download, now, reaperOptions, reapCandidates);
                        }

                        var previousDownload = previousDownloads.FirstOrDefault(d => d.Id == download.Id);
                        if (previousDownload == null)
                        {
                            continue;
                        }

                        await TriggerCallbacks(client, download, previousDownload, cancellationToken);
                    }

                    // Reap stalled downloads as a separate pass AFTER the fetch loop so the Failed
                    // transition does not re-enter TriggerCallbacks/OnDownloadFailed (which would
                    // double-remove and possibly auto-search the same dead torrent).
                    if (reaperOptions.Enabled && reapCandidates.Count > 0)
                    {
                        await ReapStalledDownloadsAsync(client, reapCandidates, reaperOptions, scope, cancellationToken);
                    }
                }
                catch (DownloadClientAdapterPollingException exception)
                {
                    logger.LogWarning(exception.Message);
                    ScheduleNextClientPollOnFailure(client);
                    continue;
                }
                catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
                {
                    logger.LogWarning(exception, $"Timeout polling download client {client.Id}; will retry on next schedule");
                    ScheduleNextClientPollOnFailure(client);
                    continue;
                }

                ScheduleNextClientPollOnSuccess(client);
            }
        }

        /// <summary>
        /// Update a download's in-memory progress snapshot and, if it has been stalled past the
        /// configured timeout, add it to <paramref name="reapCandidates"/>. Pure decision logic lives
        /// in <see cref="StalledDownloadReaper.Evaluate"/>; this method only owns the snapshot store.
        /// </summary>
        private void EvaluateStallSnapshot(Download download, DateTime now, StalledReaperOptions options, List<Download> reapCandidates)
        {
            (decimal Progress, DateTime At)? previous =
                _stallSnapshots.TryGetValue(download.Id, out var snap) ? snap : null;

            // Pass the client-reported state so an item merely QUEUED in the client (NZBGet processes
            // its queue serially; qBittorrent caps active torrents) is never reaped while it waits.
            var clientState = download.GetMetadataString("ClientState");
            var evaluation = StalledDownloadReaper.Evaluate(
                download.Status, download.Progress, previous, now, options.TimeoutMinutes, clientState);

            _stallSnapshots[download.Id] = (evaluation.SnapshotProgress, evaluation.SnapshotAt);

            if (evaluation.ShouldReap)
            {
                reapCandidates.Add(download);
            }
        }

        /// <summary>
        /// Acts on downloads the stall timer flagged as dead. Defaults to a non-destructive DRY-RUN
        /// that only reports what would be reaped. When armed (dry-run disabled) it transitions each
        /// download to Failed (excluding it from every recurring active loop), removes it from the
        /// download client, and records a history entry. It deliberately does NOT auto-search, so a
        /// dead torrent is not immediately re-grabbed.
        /// </summary>
        private async Task ReapStalledDownloadsAsync(
            DownloadClientConfiguration client,
            List<Download> candidates,
            StalledReaperOptions options,
            IServiceScope scope,
            CancellationToken cancellationToken)
        {
            // Each sample line carries the diagnostics Kevin needs to sign off: the last-known client
            // state (stalledDL / metaDL / queuedDL / "unmatched" ghost), current progress, and how
            // long the row has existed. A row showing state=queuedDL is the one recoverable case to
            // eyeball before arming.
            var samples = string.Join("; ", candidates.Take(10).Select(DescribeReapCandidate));

            if (options.DryRun)
            {
                logger.LogWarning(
                    "[STALL-REAPER][DRY-RUN] Would reap {Count} stalled download(s) on client {ClientName} " +
                    "(no progress for >= {Timeout} min; would removeFromClient=true, deleteFiles={DeleteFiles}). " +
                    "Set {DryRunEnv}=false to arm removal. Sample: {Samples}",
                    candidates.Count, client.Name, options.TimeoutMinutes, options.DeleteFiles,
                    StalledReaperOptions.DryRunEnv, samples);
                return;
            }

            logger.LogWarning(
                "[STALL-REAPER] Reaping {Count} stalled download(s) on client {ClientName} " +
                "(no progress for >= {Timeout} min, deleteFiles={DeleteFiles}). Sample: {Samples}",
                candidates.Count, client.Name, options.TimeoutMinutes, options.DeleteFiles, samples);

            var downloadService = scope.ServiceProvider.GetRequiredService<IDownloadService>();
            var downloadClientGateway = scope.ServiceProvider.GetRequiredService<IDownloadClientGateway>();
            var downloadHistoryService = scope.ServiceProvider.GetRequiredService<IDownloadHistoryService>();

            var reason = $"Reaped by stall timer: no download progress for >= {options.TimeoutMinutes} minutes";

            foreach (var download in candidates)
            {
                try
                {
                    // Remove from the client first (best-effort). Ghost rows with no live torrent
                    // simply no-op here. Partial files of an abandoned download are junk, so honour
                    // the deleteFiles option to reclaim disk space.
                    var clientItemId = download.GetExternalId();
                    if (!string.IsNullOrWhiteSpace(clientItemId))
                    {
                        await downloadClientGateway.RemoveAsync(client, clientItemId, options.DeleteFiles, cancellationToken);
                    }

                    download.Failed(reason);
                    await downloadService.UpdateAsync(download);

                    await downloadHistoryService.RecordDownloadFailedAsync(
                        download.Id,
                        download.DownloadClientId,
                        download.Title ?? "Unknown",
                        reason);

                    _stallSnapshots.TryRemove(download.Id, out _);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    logger.LogWarning(ex, "[STALL-REAPER] Failed to reap stalled download {DownloadId}", LogRedaction.SanitizeText(download.Id));
                }
            }
        }

        /// <summary>
        /// One-line diagnostic for a reap candidate used in the dry-run/armed report:
        /// title plus last-known client state, progress, and age. "unmatched" state means no live
        /// client torrent/nzb matched the row (a ghost) on the last poll.
        /// </summary>
        private static string DescribeReapCandidate(Download d)
        {
            var title = LogRedaction.SanitizeText(string.IsNullOrWhiteSpace(d.Title) ? d.Id : d.Title);
            var state = d.GetMetadataString("ClientState");
            if (string.IsNullOrWhiteSpace(state)) state = "unmatched";
            var ageHours = (DateTime.UtcNow - d.StartedAt).TotalHours;
            return $"\"{title}\" [state={state}, progress={d.Progress:0.##}%, age={ageHours:0.#}h]";
        }

        /// <summary>
        /// Compare previous and current download to trigger events
        /// </summary>
        /// <param name="client"></param>
        /// <param name="current">Current updated download</param>
        /// <param name="previous">Previous state of the download</param>
        /// <param name="cancellationToken"></param>
        /// <returns>True if the download has changed state</returns>
        private async Task TriggerCallbacks(DownloadClientConfiguration client, Download current, Download previous, CancellationToken cancellationToken = default)
        {
            switch (previous.Status, current.Status)
            {
                case var (old, next) when old == next:
                    return;
                case (_, DownloadStatus.Failed):
                    await OnDownloadFailed(current, client, current.ErrorMessage ?? "Download failed in client", cancellationToken);
                    break;
                case (_, DownloadStatus.Completed):
                    await OnDownloadCompleted(current);
                    break;
                default:
                    break;
            }
            ;

            await OnDownloadUpdated(current, cancellationToken);
        }

        /// <summary>
        /// Enqueue the processing for the given job
        /// </summary>
        /// <param name="download">Download that was completed</param>
        private async Task OnDownloadCompleted(Download download)
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var downloadProcessingJobService = scope.ServiceProvider.GetRequiredService<IDownloadProcessingJobService>();
                await downloadProcessingJobService.EnqueueAsync(download);
            }
            catch (InvalidOperationException exception)
            {
                logger.LogError($"Unexpected error after a download was detected completed: {exception.Message}, download will not be queued for importing");
            }
        }

        /// <summary>
        /// Broadcast download updates
        /// </summary>
        private async Task OnDownloadUpdated(Download download, CancellationToken cancellationToken)
        {
            logger.LogInformation($"Broadcasting download update for {download.Id}");
            await downloadPushService.HandlePushAsync(download, cancellationToken);
        }

        private async Task OnDownloadsUpdated(List<Download> downloads, CancellationToken cancellationToken)
        {
            logger.LogInformation($"Broadcasting downloads update");
            await downloadPushService.HandlePushAsync(downloads, cancellationToken);
        }

        private async Task OnDownloadFailed(
            Download download,
            DownloadClientConfiguration client,
            string errorMessage,
            CancellationToken cancellationToken)
        {
            using var scope = scopeFactory.CreateScope();
            var downloadClientGateway = scope.ServiceProvider.GetRequiredService<IDownloadClientGateway>();
            var audiobookRepository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            var downloadHistoryService = scope.ServiceProvider.GetRequiredService<IDownloadHistoryService>();
            var configurationService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
            var settings = await configurationService.GetApplicationSettingsAsync();
            var downloadService = scope.ServiceProvider.GetRequiredService<IDownloadService>();

            await downloadHistoryService.RecordDownloadFailedAsync(
                download.Id,
                download.DownloadClientId,
                download.Title ?? "Unknown",
                errorMessage);

            if (!settings.FailedDownloadHandlingEnabled)
            {
                return;
            }

            // Remove from client queue/history when handling is enabled
            var clientItemId = download.GetExternalId();
            if (!string.IsNullOrWhiteSpace(clientItemId))
            {
                await downloadClientGateway.RemoveAsync(client, clientItemId, deleteFiles: false, cancellationToken);
            }

            if (settings.FailedDownloadAutoSearch && download.AudiobookId.HasValue)
            {
                try
                {
                    var audiobook = await audiobookRepository.GetByIdAsync(download.AudiobookId!.Value);
                    if (audiobook != null && audiobook.Monitored)
                    {
                        await downloadService.SearchAndDownloadAsync(download.AudiobookId.Value);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    logger.LogDebug(ex, "Failed to auto-search after failed download {DownloadId}", download.Id);
                }
            }
        }
    }
}
