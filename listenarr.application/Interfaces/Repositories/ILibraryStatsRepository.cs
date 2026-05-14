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

namespace Listenarr.Application.Interfaces.Repositories
{
    /// <summary>
    /// Read-only aggregate queries over the library, powering the dashboard.
    /// Kept separate from <see cref="IAudiobookRepository"/> so the entity CRUD
    /// surface stays focused; this is also where future drill-down queries
    /// (the book set behind a given metric) will live.
    /// </summary>
    public interface ILibraryStatsRepository
    {
        /// <param name="activityGranularity">Bucket size for the activity time-series.</param>
        /// <param name="activityPeriods">
        /// Number of buckets in the activity window (clamped to a sane range).
        /// </param>
        Task<LibraryStats> GetLibraryStatsAsync(
            ActivityGranularity activityGranularity = ActivityGranularity.Month,
            int activityPeriods = 12,
            CancellationToken ct = default);
    }
}
