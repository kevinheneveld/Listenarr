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
using System.Text.RegularExpressions;

namespace Listenarr.Application.Audiobooks
{
    /// <summary>
    /// Normalizes per-file embedded Title tags before split clustering.
    /// Chapterized rips tag every file with its chapter ("Ch75 - The Hard
    /// Way", "The Hard Way - Chapter 12", "Track 03") — clustering those
    /// verbatim turns one book into a group per chapter (live case: a
    /// 77-file rip offered 77 one-file "books"). The chapter marker is
    /// stripped so every chapter tag collapses to the book title; a tag
    /// that is ONLY a chapter marker carries no book identity and becomes
    /// null (the file falls back to path/stem clustering).
    /// </summary>
    public static class EmbeddedTitleNormalizer
    {
        private const string MarkerWords = @"(?:ch(?:apter)?|track|part|pt|disc|disk|cd)";

        // "Ch75 - Title", "Chapter 12: Title", "Track 03 Title"
        private static readonly Regex LeadingMarker = new(
            $@"^\s*{MarkerWords}\.?\s*\d+\s*[-–—:.\s]+",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // "Title - Ch75", "Title (Part 2)", "Title, Chapter 12"
        private static readonly Regex TrailingMarker = new(
            $@"[\s,;:\-–—(\[]+{MarkerWords}\.?\s*\d+\s*[)\]]?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // A tag that is nothing but a marker ("Chapter 12", "Track 3 of 20").
        private static readonly Regex OnlyMarker = new(
            $@"^\s*{MarkerWords}\.?\s*\d+(\s*(of|/)\s*\d+)?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string? StripChapterMarkers(string? embeddedTitle)
        {
            if (string.IsNullOrWhiteSpace(embeddedTitle))
            {
                return null;
            }

            var title = embeddedTitle.Trim();
            if (OnlyMarker.IsMatch(title))
            {
                return null;
            }

            title = LeadingMarker.Replace(title, string.Empty, 1);
            title = TrailingMarker.Replace(title, string.Empty, 1);
            title = title.Trim();
            return title.Length == 0 ? null : title;
        }
    }
}
