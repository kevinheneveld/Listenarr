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

namespace Listenarr.Domain.Audiobooks
{
    /// <summary>
    /// Durable shadow of an in-memory verification job. The queue itself is a
    /// singleton channel that dies with the process — jobs queued behind a
    /// long transcription were silently lost on every container restart (two
    /// live losses on deploy day). Rows are written on enqueue, advanced as
    /// the worker progresses, and re-enqueued at startup when still pending.
    /// </summary>
    public class VerificationJobRecord
    {
        /// <summary>Matches the in-memory VerificationJob id at enqueue time.</summary>
        public Guid Id { get; set; }

        /// <summary>JSON int array of the book ids; null = whole-library walk.</summary>
        public string? AudiobookIdsJson { get; set; }

        public string Trigger { get; set; } = "manual";
        public string Status { get; set; } = "Queued";
        public DateTime EnqueuedAt { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }
    }
}
