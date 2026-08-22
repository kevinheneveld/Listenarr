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

using Listenarr.Application.Audiobooks.Organizing;
using Listenarr.Domain.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    /// <summary>
    /// Operator recovery toolbox (ported from kevin/live's maintenance
    /// endpoints): repairs for broken move states, orphaned tracking rows,
    /// root-base rows, phantom duplicate rows, plus the library-wide metadata
    /// backfill and orphan move-staging cleanup. Every destructive operation
    /// defaults to dry-run, and recovery scans carry the
    /// SkipMissingBasePathCleanup safety belt so a recovery that finds nothing
    /// can never cascade into file deletions.
    /// </summary>
    public sealed partial class LibraryMaintenanceWorkflow
    {
        private readonly IAudiobookRepository _repo;
        private readonly IAudiobookFileRepository _fileRepository;
        private readonly IHistoryRepository _historyRepository;
        private readonly IRootFolderService _rootFolderService;
        private readonly IConfigurationService _configurationService;
        private readonly ILibraryRecoveryFilesystem _recoveryFilesystem;
        private readonly LibraryOrganizeSweepWorkflow _organizeSweep;
        private readonly IScanQueueService? _scanQueueService;
        private readonly IMoveQueueService? _moveQueueService;
        private readonly ILogger<LibraryMaintenanceWorkflow> _logger;

        public LibraryMaintenanceWorkflow(
            IAudiobookRepository repo,
            IAudiobookFileRepository fileRepository,
            IHistoryRepository historyRepository,
            IRootFolderService rootFolderService,
            IConfigurationService configurationService,
            ILibraryRecoveryFilesystem recoveryFilesystem,
            LibraryOrganizeSweepWorkflow organizeSweep,
            ILogger<LibraryMaintenanceWorkflow> logger,
            IScanQueueService? scanQueueService = null,
            IMoveQueueService? moveQueueService = null)
        {
            _repo = repo;
            _fileRepository = fileRepository;
            _historyRepository = historyRepository;
            _rootFolderService = rootFolderService;
            _configurationService = configurationService;
            _recoveryFilesystem = recoveryFilesystem;
            _organizeSweep = organizeSweep;
            _logger = logger;
            _scanQueueService = scanQueueService;
            _moveQueueService = moveQueueService;
        }

        /// <summary>
        /// Enqueue a force-metadata-refresh scan for every audiobook in the library. The worker
        /// re-extracts file metadata and backfills blank library-level fields (cover, ASIN, ISBN,
        /// series, narrator, etc.) without overwriting existing values. Returns the enqueued job IDs.
        /// </summary>
        public async Task<IActionResult> BackfillMetadataAsync(CancellationToken ct)
        {
            if (_scanQueueService == null)
            {
                return new ObjectResult(new { message = "Scan queue service is unavailable" }) { StatusCode = 503 };
            }

            List<Audiobook> audiobooks;
            try
            {
                audiobooks = (await _repo.GetAllAsync()).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Failed to enumerate audiobooks for metadata backfill");
                return new ObjectResult(new { message = "Failed to enumerate audiobooks", error = ex.Message }) { StatusCode = 500 };
            }

            var enqueued = new List<object>();
            foreach (var ab in audiobooks)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    // skipMissingBasePathCleanup=true keeps a single stale path from cascading
                    // into AudiobookFile deletions during a library-wide sweep.
                    var jobId = await _scanQueueService.EnqueueScanAsync(
                        new ScanEnqueueCommand(
                            ab,
                            ForceMetadataRefresh: true,
                            SkipMissingBasePathCleanup: true));
                    enqueued.Add(new { audiobookId = ab.Id, jobId });
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to enqueue backfill scan for audiobook {AudiobookId}", ab.Id);
                }
            }

            _logger.LogInformation("Enqueued {Count} metadata-backfill scan jobs across {Total} audiobooks", enqueued.Count, audiobooks.Count);
            return new AcceptedResult((string?)null, new { message = $"Enqueued {enqueued.Count} backfill jobs", total = audiobooks.Count, enqueued });
        }

        /// <summary>
        /// Cancel Queued/Processing move jobs older than the threshold — the escape hatch for
        /// jobs wedged by a crashed worker.
        /// </summary>
        public async Task<IActionResult> CancelStaleMoveJobsAsync(int olderThanMinutes, CancellationToken ct)
        {
            if (_moveQueueService == null)
            {
                return new ObjectResult(new { message = "Move queue not available" }) { StatusCode = 503 };
            }
            if (olderThanMinutes < 0) olderThanMinutes = 0;
            var cancelled = await _moveQueueService.CancelStalePendingAsync(TimeSpan.FromMinutes(olderThanMinutes), ct);
            return new OkObjectResult(new { cancelled, olderThanMinutes });
        }

        /// <summary>
        /// Find (and optionally delete) orphan staging directories left behind by interrupted
        /// moves under every configured root folder.
        /// </summary>
        public async Task<IActionResult> CleanupOrphanMoveTmpAsync(bool dryRun, CancellationToken ct)
        {
            var rootFolders = await _rootFolderService.GetAllAsync();

            var candidates = new List<object>();
            long bytesTotal = 0;
            var deleted = 0;
            var failed = 0;
            var warnings = new List<string>();

            foreach (var root in rootFolders)
            {
                ct.ThrowIfCancellationRequested();
                var path = root.Path;
                if (string.IsNullOrWhiteSpace(path))
                {
                    continue;
                }

                IReadOnlyList<string> orphans;
                try
                {
                    orphans = _recoveryFilesystem.EnumerateOrphanMoveTempDirs(path);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    warnings.Add($"Enumeration failed under {path}: {ex.Message}");
                    continue;
                }

                foreach (var orphan in orphans)
                {
                    ct.ThrowIfCancellationRequested();
                    var size = _recoveryFilesystem.DirectorySizeBytes(orphan);
                    bytesTotal += size;
                    candidates.Add(new { path = orphan, sizeBytes = size });

                    if (!dryRun)
                    {
                        try
                        {
                            _recoveryFilesystem.DeleteDirectoryRecursive(orphan);
                            deleted++;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            failed++;
                            warnings.Add($"Failed to delete {orphan}: {ex.Message}");
                        }
                    }
                }
            }

            _logger.LogInformation(
                "Orphan move-tmp cleanup: found {Found} candidate(s), dryRun={DryRun}, deleted={Deleted}, failed={Failed}",
                candidates.Count, dryRun, deleted, failed);

            return new OkObjectResult(new
            {
                dryRun,
                found = candidates.Count,
                totalBytes = bytesTotal,
                deleted,
                failed,
                warnings,
                candidates,
            });
        }
    }
}
