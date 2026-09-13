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

namespace Listenarr.Domain.Audiobooks
{
    /// <summary>
    /// The user's answer to "you own books from this series but aren't collecting it":
    /// today only "dismissed" (not interested). Kept as a small string so a future state
    /// (e.g. "later") needs no migration. Keyed by the normalized series name; a series
    /// that later becomes monitored simply stops being a triage candidate.
    /// </summary>
    public class SeriesTriageDecision
    {
        public const string Dismissed = "dismissed";

        [Key]
        public int Id { get; set; }

        public string SeriesName { get; set; } = string.Empty;

        public string SeriesNameNormalized { get; set; } = string.Empty;

        public string? SeriesAsin { get; set; }

        public string Decision { get; set; } = Dismissed;

        public string? Note { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
