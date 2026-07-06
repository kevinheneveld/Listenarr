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

using Listenarr.Application.Downloads;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Downloads.Monitoring
{
    /// <summary>
    /// Stalled-download reaper half of the monitor processor. Detection is a
    /// progress-stall timer (see <see cref="StalledDownloadReaper"/> for the
    /// pure decision logic and rationale); this partial owns the in-memory
    /// snapshot store and the removal action. Operator-armed via environment
    /// variables (see <see cref="StalledReaperOptions"/>) — OFF by default,
    /// and DRY-RUN (report only) until explicitly disarmed.
    /// </summary>
    public partial class DownloadMonitorProcessor
    {
        // Stalled-download reaper: remembers the last observed progress (and when it was observed)
        // per download Id so we can detect downloads that make no progress for a configured timeout.
        // In-memory (like _nextClientPoll): a restart simply restarts each download's stall window.
        internal readonly System.Collections.Concurrent.ConcurrentDictionary<string, (decimal Progress, DateTime At)> _stallSnapshots = new();

        /// <summary>
        /// Drop stall snapshots for downloads that are no longer active (imported, removed, etc.)
        /// so the in-memory store can't grow unbounded.
        /// </summary>
        private void PruneStallSnapshots(IReadOnlyCollection<Download> activeDownloads)
        {
            if (_stallSnapshots.IsEmpty)
            {
                return;
            }

            var activeIds = activeDownloads.Select(d => d.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var staleId in _stallSnapshots.Keys.Where(k => !activeIds.Contains(k)).ToList())
            {
                _stallSnapshots.TryRemove(staleId, out _);
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
            // Each sample line carries the diagnostics an operator needs to sign off: the last-known
            // client state (stalledDL / metaDL / queuedDL / "unmatched" ghost), current progress, and
            // how long the row has existed. A row showing state=queuedDL is the one recoverable case
            // to eyeball before arming.
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
    }
}
