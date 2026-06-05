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
using Listenarr.Domain.Common;
using Listenarr.Domain.Models;

namespace Listenarr.Application.Search
{
    /// <summary>
    /// Builds indexer search queries from audiobook metadata. The naive "Title + author + series"
    /// approach over-specifies: an audiobook title carries a volume suffix (", Book 4"), an edition
    /// tag ("(Unabridged)"), and the series name is frequently already inside the title — so the
    /// raw query becomes e.g. "Harry Potter and the Goblet of Fire, Book 4 J.K. Rowling Harry Potter",
    /// which AND-token indexers match against nothing. This produces a clean "&lt;title&gt; &lt;author&gt;"
    /// query (volume suffix + edition tags stripped, series dropped) plus a title-only fallback.
    /// </summary>
    public static class SearchQueryBuilder
    {
        // Trailing volume markers: ", Book 4", ": Book 4", "Book 4", "Volume 2", "Vol. 3",
        // "Part 1", "Episode 5". The optional leading [,:] swallows the separator too.
        private static readonly Regex VolumeSuffix = new(
            @"(?:[,:]\s*)?\b(?:book|vol(?:ume)?|part|episode)\b\.?\s*\d+\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Hash-style series numbering: "#5".
        private static readonly Regex HashNumber = new(@"#\s*\d+\b", RegexOptions.Compiled);

        /// <summary>Primary query: cleaned title + first author. Series is intentionally omitted.</summary>
        public static string Build(Audiobook audiobook)
        {
            if (audiobook == null) return string.Empty;
            return Compose(CleanTitle(audiobook.Title), FirstAuthor(audiobook));
        }

        /// <summary>Relaxed fallback used when the primary query returns nothing: title only.</summary>
        public static string BuildTitleOnly(Audiobook audiobook)
        {
            if (audiobook == null) return string.Empty;
            return CleanTitle(audiobook.Title);
        }

        /// <summary>
        /// Strips the volume suffix and edition/format noise from a title so it matches how
        /// release titles actually read. Reuses <see cref="TitleUtils.NormalizeTitle"/> for the
        /// parenthetical/bracket/format-word cleanup, then collapses whitespace.
        /// </summary>
        public static string CleanTitle(string? title)
        {
            if (string.IsNullOrWhiteSpace(title)) return string.Empty;

            var cleaned = VolumeSuffix.Replace(title, " ");
            cleaned = HashNumber.Replace(cleaned, " ");
            // NormalizeTitle removes (...) / [...] / {...} and format words (unabridged, mp3, …),
            // turns separators into spaces, and trims.
            cleaned = TitleUtils.NormalizeTitle(cleaned);
            return cleaned.Trim();
        }

        private static string FirstAuthor(Audiobook audiobook)
        {
            var author = audiobook.Authors?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a))?.Trim();
            return author ?? string.Empty;
        }

        private static string Compose(string title, string author)
        {
            var parts = new List<string>(2);
            if (!string.IsNullOrWhiteSpace(title)) parts.Add(title);
            // Only add the author if it isn't already present in the title (some titles read
            // "Title by Author"), to avoid duplicate tokens.
            if (!string.IsNullOrWhiteSpace(author) &&
                title.IndexOf(author, StringComparison.OrdinalIgnoreCase) < 0)
            {
                parts.Add(author);
            }
            return string.Join(" ", parts);
        }
    }
}
