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
    // Series triage: the series you own books from but aren't collecting, and the
    // per-series "not interested" decision. Split out to keep LibraryController.cs
    // under the architecture size cap.
    public partial class LibraryController
    {
        /// <summary>
        /// Series with owned (file-backed) books that are not monitored, with
        /// the dashboard's ownership numbers, so the user can decide whether to
        /// collect the rest. Dismissed series are excluded unless
        /// <paramref name="includeDismissed"/> is true.
        /// </summary>
        [HttpGet("series/triage")]
        [ProducesResponseType(typeof(LibrarySeriesTriageWorkflow.SeriesTriageResponse), StatusCodes.Status200OK)]
        public async Task<ActionResult<LibrarySeriesTriageWorkflow.SeriesTriageResponse>> GetSeriesTriage(
            [FromQuery] bool includeDismissed,
            CancellationToken ct)
        {
            if (_seriesTriageWorkflow == null)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, "Series triage is not available.");
            }

            return Ok(await _seriesTriageWorkflow.GetAsync(includeDismissed, ct));
        }

        /// <summary>Record "not interested" for a series so it leaves the triage list.</summary>
        [HttpPost("series/triage/dismiss")]
        public async Task<IActionResult> DismissSeriesTriage(
            [FromBody] LibrarySeriesTriageWorkflow.SeriesTriageDecisionRequest request,
            CancellationToken ct)
        {
            if (_seriesTriageWorkflow == null)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, "Series triage is not available.");
            }

            return await _seriesTriageWorkflow.DismissAsync(request, ct);
        }

        /// <summary>Clear a series' "not interested" decision so it returns to the triage list.</summary>
        [HttpDelete("series/triage/dismiss")]
        public async Task<IActionResult> UndismissSeriesTriage(
            [FromBody] LibrarySeriesTriageWorkflow.SeriesTriageDecisionRequest request,
            CancellationToken ct)
        {
            if (_seriesTriageWorkflow == null)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, "Series triage is not available.");
            }

            return await _seriesTriageWorkflow.UndismissAsync(request, ct);
        }
    }
}
