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
using Listenarr.Application.Repositories;

namespace Listenarr.Api.Controllers
{
    [ApiController]
    [Route("api/v{version:apiVersion}/dashboard")]
    [Tags("Dashboard")]
    public class DashboardController : ControllerBase
    {
        private readonly ILibraryStatsRepository _libraryStats;
        private readonly ILogger<DashboardController> _logger;

        public DashboardController(
            ILibraryStatsRepository libraryStats,
            ILogger<DashboardController> logger)
        {
            _libraryStats = libraryStats;
            _logger = logger;
        }

        /// <summary>
        /// Get aggregate metrics for the whole library: overview counts,
        /// metadata completeness, series completeness, genre/duration/language
        /// distributions, quality breakdown, author coverage, and recent activity.
        /// </summary>
        [HttpGet("stats")]
        public async Task<IActionResult> GetStats(CancellationToken ct)
        {
            var stats = await _libraryStats.GetLibraryStatsAsync(ct);
            _logger.LogDebug(
                "Dashboard stats computed: {TotalBooks} books, {TotalSeries} series",
                stats.Overview.TotalBooks, stats.Series.TotalSeries);
            return Ok(stats);
        }
    }
}
