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

using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    /// <summary>
    /// Resolve duplicate records (kevin/live semantics, ASIN-strict): each merge
    /// pair names a winner and losers that share the winner's ASIN. Losers'
    /// files and folders are deleted from disk (the winner already owns the
    /// good copy), their downloads/history/move jobs are reassigned to the
    /// winner, and the loser rows are removed. The clear-ASIN list is the
    /// escape hatch for "same ASIN but actually different books" — those rows
    /// keep everything and merely lose the ASIN so the duplicate scan stops
    /// flagging them.
    /// </summary>
    public sealed class LibraryDuplicatesMergeWorkflow
    {
        private readonly IAudiobookRepository _repo;
        private readonly IAudiobookFilesystemDeleteService _filesystemDeleteService;
        private readonly DashboardAggregateCache _aggregateCache;
        private readonly ILogger<LibraryDuplicatesMergeWorkflow> _logger;

        public LibraryDuplicatesMergeWorkflow(
            IAudiobookRepository repo,
            IAudiobookFilesystemDeleteService filesystemDeleteService,
            DashboardAggregateCache aggregateCache,
            ILogger<LibraryDuplicatesMergeWorkflow> logger)
        {
            _repo = repo;
            _filesystemDeleteService = filesystemDeleteService;
            _aggregateCache = aggregateCache;
            _logger = logger;
        }

        public async Task<IActionResult> MergeAsync(LibraryController.MergeDuplicatesRequest? request, CancellationToken ct)
        {
            if (request?.Merges == null || request.Merges.Count == 0)
            {
                return new BadRequestObjectResult(new { message = "No merges provided" });
            }

            var nonEmptyMerges = request.Merges
                .Where(m =>
                    (m.LoserIds != null && m.LoserIds.Any(id => id != (m.WinnerId ?? -1))) ||
                    (m.ClearAsinIds != null && m.ClearAsinIds.Count > 0))
                .ToList();
            if (nonEmptyMerges.Count == 0)
            {
                return new BadRequestObjectResult(new { message = "No actionable merges or clear-ASIN entries" });
            }

            // ASIN validation pass: a winner must carry an ASIN, and every loser /
            // clear-ASIN row must carry the SAME one — the whole point of the merge
            // is "these rows are the same edition"; anything else is refused.
            var allIds = nonEmptyMerges
                .SelectMany(m =>
                    (m.WinnerId.HasValue ? new[] { m.WinnerId.Value } : Array.Empty<int>())
                    .Concat(m.LoserIds ?? new List<int>())
                    .Concat(m.ClearAsinIds ?? new List<int>()))
                .Distinct()
                .ToList();

            var rowsById = new Dictionary<int, Audiobook>();
            foreach (var id in allIds)
            {
                var row = await _repo.GetByIdAsync(id);
                if (row != null) rowsById[id] = row;
            }

            foreach (var pair in nonEmptyMerges)
            {
                var losers = (pair.LoserIds ?? new List<int>()).Distinct().Where(id => id != (pair.WinnerId ?? -1)).ToList();
                var clears = (pair.ClearAsinIds ?? new List<int>()).Distinct().ToList();

                string referenceAsin;
                int referenceId;
                if (pair.WinnerId.HasValue)
                {
                    if (!rowsById.TryGetValue(pair.WinnerId.Value, out var w))
                    {
                        return new BadRequestObjectResult(new { message = $"Winner id {pair.WinnerId} not found" });
                    }
                    referenceAsin = (w.Asin ?? string.Empty).Trim().ToUpperInvariant();
                    if (string.IsNullOrEmpty(referenceAsin))
                    {
                        return new BadRequestObjectResult(new { message = $"Winner id {pair.WinnerId} has no ASIN — refusing to merge without one" });
                    }
                    referenceId = pair.WinnerId.Value;
                }
                else if (losers.Count > 0)
                {
                    return new BadRequestObjectResult(new { message = "Pair has losers but no winnerId — losers must merge into a winner" });
                }
                else
                {
                    var firstClearId = clears[0];
                    if (!rowsById.TryGetValue(firstClearId, out var c))
                    {
                        return new BadRequestObjectResult(new { message = $"Clear-ASIN id {firstClearId} not found" });
                    }
                    referenceAsin = (c.Asin ?? string.Empty).Trim().ToUpperInvariant();
                    if (string.IsNullOrEmpty(referenceAsin))
                    {
                        return new BadRequestObjectResult(new { message = $"Clear-ASIN id {firstClearId} already has no ASIN" });
                    }
                    referenceId = firstClearId;
                }

                foreach (var otherId in losers.Concat(clears))
                {
                    if (!rowsById.TryGetValue(otherId, out var other))
                    {
                        return new BadRequestObjectResult(new { message = $"Id {otherId} not found" });
                    }
                    var otherAsin = (other.Asin ?? string.Empty).Trim().ToUpperInvariant();
                    if (otherAsin != referenceAsin)
                    {
                        return new BadRequestObjectResult(new
                        {
                            message = $"Id {otherId} ASIN {otherAsin} does not match reference id {referenceId} ASIN {referenceAsin}"
                        });
                    }
                }
            }

            var result = new LibraryController.MergeDuplicatesResult();

            // Disk first: the losers' files/folders are redundant copies; remove them
            // before the DB merge so a failed delete surfaces as a warning while the
            // rows are still inspectable.
            var allLoserIds = nonEmptyMerges
                .SelectMany(p => (p.LoserIds ?? new List<int>()).Distinct().Where(id => id != (p.WinnerId ?? -1)))
                .Distinct()
                .ToList();
            foreach (var loserId in allLoserIds)
            {
                if (!rowsById.TryGetValue(loserId, out var loser)) continue;
                try
                {
                    var fsResult = await _filesystemDeleteService.DeleteAsync(loser, deleteFolder: true);
                    result.DiskFilesDeleted += fsResult.DeletedFiles;
                    if (fsResult.DeletedFolder) result.DiskFoldersDeleted++;
                    foreach (var w in fsResult.Warnings)
                    {
                        result.Warnings.Add($"audiobook id {loser.Id}: {w}");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Filesystem cleanup failed for discarded audiobook {Id}", loserId);
                    result.Warnings.Add($"audiobook id {loserId}: filesystem cleanup failed: {ex.Message}");
                }
            }

            foreach (var pair in nonEmptyMerges)
            {
                if (ct.IsCancellationRequested) break;
                var losers = (pair.LoserIds ?? new List<int>()).Distinct().Where(id => id != (pair.WinnerId ?? -1)).ToList();
                var clears = (pair.ClearAsinIds ?? new List<int>()).Distinct().Except(losers).ToList();
                if (losers.Count == 0 && clears.Count == 0) continue;

                if (losers.Count > 0 && pair.WinnerId.HasValue)
                {
                    var counts = await _repo.MergeAudiobookRowsAsync(pair.WinnerId.Value, losers, ct);
                    result.DownloadsReassigned += counts.DownloadsReassigned;
                    result.HistoryReassigned += counts.HistoryReassigned;
                    result.MoveJobsReassigned += counts.MoveJobsReassigned;
                    result.RowsDeleted += counts.RowsDeleted;
                }

                if (clears.Count > 0)
                {
                    result.AsinsCleared += await _repo.ClearAsinsAsync(clears, ct);
                }

                result.GroupsProcessed++;
            }

            _logger.LogInformation(
                "Resolved {Groups} duplicate audiobook groups: deleted {Deleted} rows, cleared {Cleared} ASINs, removed {DiskFiles} files / {DiskFolders} folders from disk, reassigned {Downloads} downloads / {History} history / {MoveJobs} move jobs",
                result.GroupsProcessed, result.RowsDeleted, result.AsinsCleared,
                result.DiskFilesDeleted, result.DiskFoldersDeleted,
                result.DownloadsReassigned, result.HistoryReassigned, result.MoveJobsReassigned);

            _aggregateCache.InvalidateAll(); // post-merge duplicate re-scan must not see merged rows
            return new OkObjectResult(result);
        }
    }
}
