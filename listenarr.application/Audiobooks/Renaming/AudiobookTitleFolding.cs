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
using Listenarr.Application.Search;

namespace Listenarr.Application.Audiobooks.Renaming
{
    /// <summary>
    /// THE single rule for what the <c>{Title}</c> naming token expands to.
    /// Every path producer (the rename flow behind the book page's "Organize
    /// Files", the organize sweep's path planner, add/move) must share it —
    /// they used to disagree, so a book organized from its detail page still
    /// showed under "Will move" in the library-wide sweep (live case: the
    /// per-book flow filed "The Horse and His Boy - The Chronicles of
    /// Narnia" while the sweep's canonical path said plain "The Horse and
    /// His Boy").
    ///
    /// The rule: fold the subtitle into the title ("Title: Subtitle") when it
    /// carries information the path doesn't already have — Audible hides the
    /// volume number of some series in the subtitle ("Favorite Science
    /// Fiction Stories" / "Volume 3"), and without folding, sibling volumes
    /// collide into ONE folder. But skip folding when the subtitle merely
    /// echoes the series ("Prince Caspian" / "The Chronicles of Narnia" under
    /// series "The Chronicles of Narnia (Publication Order)") — the series
    /// folder above already says it. Patterns that place <c>{Subtitle}</c>
    /// explicitly always get the plain title.
    /// </summary>
    public static class AudiobookTitleFolding
    {
        public static string CombinedTitle(
            string? title,
            string? subtitle,
            string? series,
            bool patternsUseSubtitleToken)
        {
            var safeTitle = string.IsNullOrWhiteSpace(title) ? "Unknown Title" : title!;
            if (patternsUseSubtitleToken || string.IsNullOrWhiteSpace(subtitle))
            {
                return safeTitle;
            }

            if (safeTitle.Contains(subtitle!, StringComparison.OrdinalIgnoreCase))
            {
                return safeTitle;
            }

            if (SubtitleEchoesSeries(subtitle, series))
            {
                return safeTitle;
            }

            return $"{safeTitle}: {subtitle}";
        }

        /// <summary>
        /// True when the subtitle is redundant with the series name (either
        /// contains the other, normalized) — such a subtitle adds nothing to a
        /// path that already carries a series folder, and folding it produced
        /// folders like "Prince Caspian - The Chronicles of Narnia" INSIDE
        /// ".../The Chronicles of Narnia (Publication Order)/". Public + pure
        /// for unit testing.
        /// </summary>
        public static bool SubtitleEchoesSeries(string? subtitle, string? series)
        {
            if (string.IsNullOrWhiteSpace(subtitle) || string.IsNullOrWhiteSpace(series))
            {
                return false;
            }

            var normalizedSubtitle = TitleMatcher.Normalize(subtitle);
            var normalizedSeries = TitleMatcher.Normalize(series);
            if (normalizedSubtitle.Length == 0 || normalizedSeries.Length == 0)
            {
                return false;
            }

            return normalizedSeries.Contains(normalizedSubtitle, StringComparison.Ordinal)
                || normalizedSubtitle.Contains(normalizedSeries, StringComparison.Ordinal);
        }
    }
}
