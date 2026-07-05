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

using Listenarr.Application.Common.Images;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    public partial class LibraryController
    {
        private readonly IExternalCoverArtSweepService? _externalCoverArtSweepService;

        /// <summary>
        /// One-shot admin sweep: downloads any audiobook cover still pointing at
        /// an external http(s) URL into local library storage. Records already
        /// pointing at the local cache are skipped, so re-running after a
        /// successful sweep is a no-op. Per-record failures (download 404, IO
        /// error, etc.) are logged and counted but do not abort the sweep.
        /// </summary>
        /// <param name="ct">Cancellation token bound to the request.</param>
        [HttpPost("cache-external-covers")]
        public async Task<IActionResult> CacheExternalCovers(CancellationToken ct)
        {
            if (_externalCoverArtSweepService == null)
            {
                return StatusCode(503, new { message = "External cover-art sweep service is unavailable" });
            }

            ExternalCoverArtSweepResult result;
            try
            {
                result = await _externalCoverArtSweepService.SweepAsync(ct);
            }
            catch (OperationCanceledException)
            {
                return StatusCode(499, new { message = "Sweep cancelled" });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                // The sweep service logs the failure with full context.
                return StatusCode(500, new { message = "External cover-art sweep failed", error = ex.Message });
            }

            return Ok(new
            {
                message = $"Sweep complete: cached {result.Succeeded} of {result.Queued} external covers",
                totalScanned = result.TotalScanned,
                alreadyLocal = result.AlreadyLocal,
                queued = result.Queued,
                succeeded = result.Succeeded,
                failed = result.Failed,
                durationMs = result.DurationMs,
            });
        }
    }
}
