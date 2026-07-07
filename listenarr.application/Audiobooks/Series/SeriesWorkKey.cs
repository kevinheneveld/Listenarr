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
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Listenarr.Application.Audiobooks.Series
{
    /// <summary>
    /// Collapses the many Audible catalog entries for the same book — regional
    /// re-releases, narrators, dramatizations — into one logical "work" key, so
    /// series totals count books instead of recordings (live case: "Jack
    /// Reacher 23/97" where the catalog's 97 entries cover ~29 actual books).
    /// Server-side mirror of the frontend's buildWorkKey (seriesDisplay.ts):
    /// when a series position is known it is folded into the key — different
    /// positions never merge (volume-numbered sets stay distinct), while
    /// editions of the same book (same position) collapse.
    /// </summary>
    public static class SeriesWorkKey
    {
        private static readonly Regex BookNumberRegex = new(@"\bbook\s+\d+\b", RegexOptions.Compiled);
        private static readonly Regex TrailingNumberRegex = new(@"\s+\d+$", RegexOptions.Compiled);
        private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
        private static readonly Regex OrphanedPossessiveRegex = new(@"^s\s+", RegexOptions.Compiled);

        /// <summary>
        /// Lowercase, diacritic-folded, non-alphanumerics collapsed to single
        /// spaces — mirror of the frontend's normalizeCollectionText.
        /// </summary>
        public static string NormalizeText(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            var folded = value.Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(folded.Length);
            var lastWasSpace = true;
            foreach (var ch in folded)
            {
                var category = CharUnicodeInfo.GetUnicodeCategory(ch);
                if (category == UnicodeCategory.NonSpacingMark) continue;

                if (char.IsLetterOrDigit(ch) && ch < 128)
                {
                    sb.Append(char.ToLowerInvariant(ch));
                    lastWasSpace = false;
                }
                else if (char.IsLetterOrDigit(ch))
                {
                    // Non-ASCII letters/digits that survived folding: keep them
                    // lowercased so non-Latin titles still key deterministically.
                    sb.Append(char.ToLowerInvariant(ch));
                    lastWasSpace = false;
                }
                else if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
            }

            return sb.ToString().Trim();
        }

        /// <summary>
        /// Canonicalize a series position so the same slot matches across
        /// sources ("1" / "1.0" / "01" → "1", "4.5" → "4.5"); non-numeric
        /// labels fall back to lowercase.
        /// </summary>
        public static string NormalizePosition(string? position)
        {
            if (string.IsNullOrWhiteSpace(position)) return string.Empty;
            var trimmed = position.Trim();
            if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
                && double.IsFinite(num))
            {
                return num.ToString("0.####", CultureInfo.InvariantCulture);
            }

            return trimmed.ToLowerInvariant();
        }

        /// <summary>
        /// Builds the work key: subtitle dropped (text after the first colon —
        /// usually series/edition info), author/brand prefix stripped, "book N"
        /// and trailing list numbers removed, then the normalized series
        /// position appended when present so distinct volumes never merge.
        /// </summary>
        public static string Build(string? title, IReadOnlyCollection<string>? authors, string? seriesPosition)
        {
            var key = title ?? string.Empty;
            var colon = key.IndexOf(':');
            if (colon > 0)
            {
                key = key[..colon]; // drop subtitle (usually series/edition info)
            }

            key = NormalizeText(key);
            foreach (var author in authors ?? Array.Empty<string>())
            {
                var an = NormalizeText(author);
                if (an.Length == 0) continue;
                if (key == an || key.StartsWith(an + " ", StringComparison.Ordinal))
                {
                    key = key[an.Length..].Trim();
                    key = OrphanedPossessiveRegex.Replace(key, string.Empty); // "Author's Title"
                }
            }

            key = BookNumberRegex.Replace(key, " ");
            key = TrailingNumberRegex.Replace(key, string.Empty);
            key = WhitespaceRegex.Replace(key, " ").Trim();

            var baseKey = key.Length > 0 ? key : NormalizeText(title);
            var pos = NormalizePosition(seriesPosition);
            return pos.Length > 0 ? $"{baseKey}#{pos}" : baseKey;
        }
    }
}
