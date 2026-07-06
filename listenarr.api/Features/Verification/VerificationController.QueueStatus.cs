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

namespace Listenarr.Api.Features.Verification
{
    public partial class VerificationController
    {
        /// <summary>
        /// Live status of the verification queue and series-catalog backfill for
        /// the dashboard's background-activity panel: queue depth, the book being
        /// transcribed right now, today's throughput, and a drain ETA computed
        /// from recent per-book pace.
        /// </summary>
        /// <param name="ct">Cancellation token bound to the request.</param>
        [HttpGet("queue-status")]
        public async Task<IActionResult> GetQueueStatus(CancellationToken ct)
        {
            return await _queueStatusWorkflow.StatusAsync(ct);
        }
    }
}
