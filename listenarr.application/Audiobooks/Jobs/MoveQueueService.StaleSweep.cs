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

namespace Listenarr.Application.Audiobooks.Jobs
{
    public partial class MoveQueueService
    {
        /// <summary>
        /// Mark Queued/Processing rows whose last activity is older than the
        /// threshold as Cancelled so the maintenance sweep can clear stuck
        /// pending moves. The queue is persistence-backed, so no in-memory
        /// eviction is needed for a re-queue to escape deduplication.
        /// </summary>
        public async Task<int> CancelStalePendingAsync(TimeSpan staleThreshold, CancellationToken ct = default)
        {
            var cutoff = _timeProvider.GetUtcNow() - staleThreshold;
            var cancelledIds = await _persistence.CancelStalePendingAsync(
                cutoff,
                "Cancelled by operator (stale pending sweep)",
                ct);

            if (cancelledIds.Count > 0)
            {
                _logger.LogInformation("Cancelled {Count} stale pending move job(s) older than {Threshold}", cancelledIds.Count, staleThreshold);
            }

            return cancelledIds.Count;
        }
    }
}
