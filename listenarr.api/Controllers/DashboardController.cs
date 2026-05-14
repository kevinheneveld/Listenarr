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
using Listenarr.Domain.Models;

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
        /// <param name="activityGranularity">
        /// Bucket size for the activity time-series: Day, Week, or Month (default Month).
        /// </param>
        /// <param name="activityPeriods">
        /// Number of buckets in the activity window (default 12, clamped to 1-365).
        /// </param>
        /// <param name="ct">Cancellation token.</param>
        [HttpGet("stats")]
        public async Task<IActionResult> GetStats(
            [FromQuery] ActivityGranularity activityGranularity = ActivityGranularity.Month,
            [FromQuery] int activityPeriods = 12,
            CancellationToken ct = default)
        {
            var stats = await _libraryStats.GetLibraryStatsAsync(activityGranularity, activityPeriods, ct);
            _logger.LogDebug(
                "Dashboard stats computed: {TotalBooks} books, {TotalSeries} series, activity {Granularity}x{Periods}",
                stats.Overview.TotalBooks, stats.Series.TotalSeries, activityGranularity, activityPeriods);
            return Ok(stats);
        }
    }
}
