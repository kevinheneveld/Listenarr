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
using System.ComponentModel.DataAnnotations;

namespace Listenarr.Domain.Models
{
    public class MonitoredSeries
    {
        [Key]
        public int Id { get; set; }

        public string SeriesName { get; set; } = string.Empty;

        public string SeriesNameNormalized { get; set; } = string.Empty;

        public string? SeriesAsin { get; set; }

        /// <summary>
        /// True when <see cref="SeriesAsin"/> was set explicitly by the user (e.g. via the
        /// "Wrong series?" picker) and must be treated as authoritative: sync resolves the
        /// catalog by this ASIN instead of by name, and name-resolution must not overwrite it.
        /// </summary>
        public bool AsinPinned { get; set; }

        public string Region { get; set; } = "us";

        public string Language { get; set; } = "all";

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? LastCheckedAt { get; set; }

        public DateTime? LastSuccessfulSyncAt { get; set; }

        public string? LastError { get; set; }
    }
}
