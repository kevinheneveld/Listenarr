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
using Listenarr.Domain.Models;

namespace Listenarr.Application.Interfaces
{
    public interface IMoveQueueService
    {
        Task<Guid> EnqueueMoveAsync(int audiobookId, string requestedPath, string? sourcePath = null);
        Task<Guid?> RequeueMoveAsync(Guid jobId);
        bool TryGetJob(Guid id, out MoveJob? job);
        void UpdateJobStatus(Guid id, string status, string? error = null);
        System.Threading.Channels.ChannelReader<MoveJob> Reader { get; }

        /// <summary>
        /// Re-enqueue persisted <c>Queued</c> jobs (and stale <c>Processing</c>
        /// jobs older than <paramref name="staleProcessingThreshold"/>) into
        /// the in-memory channel after a process restart. The channel is
        /// volatile state — without rehydration, queued jobs in the DB have
        /// no consumer and sit forever (and <c>EnqueueMoveAsync</c>'s dedup
        /// check would silently return their orphan ids on re-queue attempts
        /// from the modal). Idempotent: a job already known to this process
        /// (already in <c>_jobs</c>) is skipped, so a bounce mid-startup
        /// can't double-enqueue. Stale <c>Processing</c> rows are flipped
        /// back to <c>Queued</c> in the DB before being re-channeled so the
        /// consumer treats them as fresh work, not as in-progress.
        /// </summary>
        /// <param name="staleProcessingThreshold">
        /// How long a <c>Processing</c> row must have gone untouched (no
        /// <c>UpdatedAt</c> bump) before it's considered abandoned and
        /// eligible for re-channeling. Typical: a few minutes — longer than
        /// the slowest realistic single-file copy, shorter than the time a
        /// human would wait before noticing "nothing's happening."
        /// </param>
        Task<int> RehydratePendingAsync(TimeSpan staleProcessingThreshold, CancellationToken ct = default);

        /// <summary>
        /// Flip every <c>Queued</c> or stale <c>Processing</c> row older than
        /// <paramref name="staleThreshold"/> to <c>Cancelled</c>. Operator
        /// escape hatch for clearing a stuck queue without restarting the
        /// process. Doesn't touch the in-memory channel — the consumer's
        /// per-job DB recheck (added alongside this method) skips channeled
        /// jobs whose DB status is no longer <c>Queued</c>, so cancelled
        /// jobs are effectively no-ops if they're picked up.
        /// </summary>
        Task<int> CancelStalePendingAsync(TimeSpan staleThreshold, CancellationToken ct = default);
    }
}

