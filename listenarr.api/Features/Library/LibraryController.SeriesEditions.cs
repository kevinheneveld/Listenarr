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
    public partial class LibraryController
    {
        /// <summary>
        /// Recording-edition ("run") view of one series: cached catalog
        /// recordings grouped into production runs with works-based coverage
        /// and add-ready metadata for each run's missing works. Heuristic
        /// grouping — Audible publishes no run identifier.
        /// </summary>
        /// <param name="name">Series name as the library spells it.</param>
        /// <param name="region">Catalog region; defaults to the configured search region.</param>
        /// <param name="ct">Cancellation token bound to the request.</param>
        [HttpGet("series/editions")]
        public async Task<IActionResult> GetSeriesEditions([FromQuery] string name, [FromQuery] string? region, CancellationToken ct)
        {
            return await _seriesEditionsWorkflow.EditionsAsync(name, region, ct);
        }
    }
}
