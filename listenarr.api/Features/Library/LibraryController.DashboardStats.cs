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

namespace Listenarr.Api.Features.Library;

public partial class LibraryController
{
    /// <summary>
    /// Dashboard stats: quality distribution (codec/bitrate over tracked files)
    /// and metadata-completeness gap counts.
    /// </summary>
    /// <param name="ct">Cancellation token bound to the request.</param>
    [HttpGet("dashboard/stats")]
    public async Task<IActionResult> GetDashboardStats(CancellationToken ct)
    {
        return await _dashboardStatsWorkflow.GetStatsAsync(ct);
    }

    /// <summary>
    /// IDs of every audiobook missing the given metadata field — the dashboard
    /// completeness panel's drill-down, kept in lockstep with its counts.
    /// </summary>
    /// <param name="field">CoverArt, Description, Narrators, or SeriesPosition.</param>
    /// <param name="ct">Cancellation token bound to the request.</param>
    [HttpGet("dashboard/missing/{field}/ids")]
    public async Task<IActionResult> GetBooksMissingField(string field, CancellationToken ct)
    {
        return await _dashboardStatsWorkflow.GetMissingIdsAsync(field, ct);
    }
}
