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
using Listenarr.Domain.Common;
using Listenarr.Domain.Models;

namespace Listenarr.Application.Audiobooks.Verification
{
    /// <summary>
    /// Picks the audio files whose opening/closing audio carries the spoken
    /// credits (ADR-0001). Multi-file books MUST use natural/track-number order
    /// ("Chapter 2" before "Chapter 10", via <see cref="AudiobookFileOrdering"/>)
    /// — alphabetical order would sample the middle of the book.
    /// </summary>
    public static class VerificationFileSelection
    {
        /// <summary>
        /// The naturally-first and naturally-last audio file of the book, falling
        /// back to the legacy single <see cref="Audiobook.FilePath"/> when no file
        /// rows exist. Both null when the book has no audio at all.
        /// </summary>
        public static (string? FirstFile, string? LastFile) SelectFirstAndLast(Audiobook audiobook)
        {
            var ordered = AudiobookFileOrdering.InNaturalOrder(audiobook.Files)
                .Select(f => f.Path)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();

            if (ordered.Count > 0) return (ordered[0], ordered[^1]);

            var legacy = string.IsNullOrWhiteSpace(audiobook.FilePath) ? null : audiobook.FilePath;
            return (legacy, legacy);
        }
    }
}
