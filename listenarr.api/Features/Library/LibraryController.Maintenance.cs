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
    /// Recovery/maintenance surface: operator tools for broken move states,
    /// orphaned tracking, phantom rows, orphan staging dirs, the library-wide
    /// metadata backfill, and duplicate-record resolution. Destructive
    /// operations default to dry-run.
    /// </summary>
    public partial class LibraryController
    {
        /// <summary>
        /// Enqueue a force-metadata-refresh scan for every audiobook (backfills blank
        /// fields from file tags; never overwrites existing values).
        /// </summary>
        [HttpPost("backfill-metadata")]
        public async Task<IActionResult> BackfillMetadata(CancellationToken ct)
        {
            return await _maintenanceWorkflow.BackfillMetadataAsync(ct);
        }

        /// <summary>
        /// Repair rows broken by a failed move: re-stamp BasePath from the latest
        /// "Moved" history target (when it exists on disk with content) and re-scan.
        /// Dry-run by default.
        /// </summary>
        [HttpPost("recover-broken-moves")]
        public async Task<IActionResult> RecoverBrokenMoves([FromQuery] bool dryRun = true, CancellationToken ct = default)
        {
            return await _maintenanceWorkflow.RecoverBrokenMovesAsync(dryRun, ct);
        }

        /// <summary>
        /// Repair rows whose BasePath is a bare library root: compute (or find on disk)
        /// the real folder, re-stamp, and re-scan. Dry-run by default.
        /// </summary>
        [HttpPost("recover-rootbase-audiobooks")]
        public async Task<IActionResult> RecoverRootBaseAudiobooks([FromQuery] bool dryRun = true, CancellationToken ct = default)
        {
            return await _maintenanceWorkflow.RecoverRootBaseAudiobooksAsync(dryRun, ct);
        }

        /// <summary>
        /// Re-register rows whose folder exists with audio on disk but which track zero
        /// files (orphaned tracking). Dry-run by default.
        /// </summary>
        [HttpPost("recover-orphaned-tracking")]
        public async Task<IActionResult> RecoverOrphanedTracking([FromQuery] bool dryRun = true, CancellationToken ct = default)
        {
            return await _maintenanceWorkflow.RecoverOrphanedTrackingAsync(dryRun, ct);
        }

        /// <summary>
        /// Merge phantom duplicate rows (zero files) into their file-owning sibling.
        /// Dry-run by default.
        /// </summary>
        [HttpPost("cleanup-phantom-rows")]
        public async Task<IActionResult> CleanupPhantomRows([FromQuery] bool dryRun = true, CancellationToken ct = default)
        {
            return await _maintenanceWorkflow.CleanupPhantomRowsAsync(dryRun, ct);
        }

        /// <summary>
        /// Cancel Queued/Processing move jobs whose last activity is older than the
        /// threshold (jobs wedged by a crashed worker).
        /// </summary>
        [HttpPost("move/cancel-stale")]
        public async Task<IActionResult> CancelStaleMoveJobs([FromQuery] int olderThanMinutes = 5, CancellationToken ct = default)
        {
            return await _maintenanceWorkflow.CancelStaleMoveJobsAsync(olderThanMinutes, ct);
        }

        /// <summary>
        /// Find (and optionally delete) orphan staging directories left by interrupted
        /// moves. Dry-run by default.
        /// </summary>
        [HttpPost("move/cleanup-orphan-tmp")]
        public async Task<IActionResult> CleanupOrphanMoveTmp([FromQuery] bool dryRun = true, CancellationToken ct = default)
        {
            return await _maintenanceWorkflow.CleanupOrphanMoveTmpAsync(dryRun, ct);
        }

        /// <summary>
        /// Resolve duplicate records: merge same-ASIN losers into a winner (files
        /// deleted from disk, downloads/history/move jobs reassigned, rows removed) or
        /// clear the ASIN on rows that share one but are actually different books.
        /// </summary>
        [HttpPost("duplicates/merge")]
        public async Task<IActionResult> MergeDuplicates([FromBody] MergeDuplicatesRequest request, CancellationToken ct)
        {
            return await _duplicatesMergeWorkflow.MergeAsync(request, ct);
        }
    }
}
