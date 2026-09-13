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

namespace Listenarr.Application.Audiobooks.Monitoring
{
    /// <summary>
    /// The one normalization used for series-name keys (MonitoredSeries.SeriesNameNormalized,
    /// SeriesTriageDecision.SeriesNameNormalized): accents stripped, letters/digits lower-cased,
    /// punctuation dropped, whitespace collapsed to single spaces.
    /// </summary>
    public static class SeriesNameNormalizer
    {
        public static string Normalize(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var decomposed = name.Trim().Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);
            foreach (var character in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if (char.IsLetterOrDigit(character))
                {
                    builder.Append(char.ToLowerInvariant(character));
                }
                else if (char.IsWhiteSpace(character))
                {
                    builder.Append(' ');
                }
            }

            return string.Join(
                ' ',
                builder.ToString()
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
