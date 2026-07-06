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

namespace Listenarr.Application.Audiobooks.Catalog
{
    /// <summary>
    /// The actionable metadata gaps the dashboard's completeness panel reports.
    /// SeriesPosition only applies to books that belong to a series.
    /// </summary>
    public enum MissingField
    {
        CoverArt,
        Description,
        Narrators,
        SeriesPosition,
    }

    /// <summary>
    /// Shared per-field "is this book missing X?" predicate — the same logic
    /// drives the dashboard's completeness counts and the missing/{field}/ids
    /// drill-down, so the two can never disagree. Pure and unit-testable.
    /// </summary>
    public static class LibraryMetadataGaps
    {
        public static bool MissesField(Audiobook b, MissingField field) => field switch
        {
            MissingField.CoverArt => string.IsNullOrWhiteSpace(b.ImageUrl),
            MissingField.Description => string.IsNullOrWhiteSpace(b.Description),
            MissingField.Narrators => b.Narrators == null || !b.Narrators.Any(n => !string.IsNullOrWhiteSpace(n)),
            MissingField.SeriesPosition => SeriesNameOf(b) != null && string.IsNullOrWhiteSpace(SeriesNumberOf(b)),
            _ => false,
        };

        private static string? SeriesNameOf(Audiobook book)
        {
            var primary = book.SeriesMemberships?
                .OrderByDescending(m => m.IsPrimary)
                .ThenBy(m => m.SortOrder)
                .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.SeriesName));
            if (primary != null) return primary.SeriesName;
            return string.IsNullOrWhiteSpace(book.Series) ? null : book.Series;
        }

        private static string? SeriesNumberOf(Audiobook book)
        {
            var primary = book.SeriesMemberships?
                .OrderByDescending(m => m.IsPrimary)
                .ThenBy(m => m.SortOrder)
                .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.SeriesName));
            if (primary != null) return primary.SeriesNumber;
            return book.SeriesNumber;
        }
    }
}
