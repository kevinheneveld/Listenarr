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

namespace Listenarr.Api.Services.Scoring
{
    /// <summary>
    /// Token-overlap relevance check used during automatic search scoring. Given
    /// a search result title and the audiobook being searched for, decide whether
    /// the result is plausibly the same work. Defends against indexers returning
    /// matter that shares one or two tokens with the query (e.g., a Patterson Hood
    /// concert recording being suggested for a James Patterson audiobook).
    /// </summary>
    public static class RelevanceFilter
    {
        /// <summary>
        /// Default fraction of significant audiobook tokens that must appear in the
        /// result title for the result to be considered relevant. Empirically chosen
        /// so that single-shared-token false matches (e.g., one author surname only)
        /// are rejected while multi-token legitimate matches pass.
        /// </summary>
        public const double DefaultMinRelevance = 0.30;

        // Common English stop words plus tokens that are too generic to count as
        // a meaningful match (e.g., "audiobook" appearing in both the query and
        // every audiobook indexer's titles).
        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "a", "an", "the", "and", "or", "of", "in", "on", "at", "by", "to", "for",
            "with", "is", "are", "was", "were", "be", "been", "being",
            "have", "has", "had", "do", "does", "did",
            "but", "as", "if", "then", "so", "this", "that", "these", "those",
            "it", "its", "his", "her", "he", "she", "they", "them",
            "from", "into", "out", "up", "down",
            "audiobook", "audiobooks", "ebook", "ebooks", "book", "books",
        };

        private static readonly Regex TokenSplit = new(@"[a-z0-9]+", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// Returns the lowercased, stop-word-filtered tokens of length >= 2 in the input.
        /// </summary>
        public static IEnumerable<string> SignificantTokens(string? input)
        {
            if (string.IsNullOrWhiteSpace(input)) yield break;
            foreach (Match m in TokenSplit.Matches(input))
            {
                var t = m.Value.ToLowerInvariant();
                if (t.Length < 2) continue;
                if (StopWords.Contains(t)) continue;
                yield return t;
            }
        }

        /// <summary>
        /// Computes a relevance ratio in [0, 1]: the fraction of distinct
        /// significant tokens from the audiobook's title + authors that appear
        /// in the result title.
        ///
        /// Returns 1.0 when the audiobook side has no significant tokens (cannot
        /// be judged — fail open rather than reject every result).
        /// </summary>
        public static double ComputeRelevance(
            string? resultTitle,
            string? audiobookTitle,
            IEnumerable<string>? authors)
        {
            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in SignificantTokens(audiobookTitle)) expected.Add(t);
            if (authors != null)
            {
                foreach (var author in authors)
                {
                    foreach (var t in SignificantTokens(author)) expected.Add(t);
                }
            }
            if (expected.Count == 0) return 1.0;

            var resultTokens = new HashSet<string>(SignificantTokens(resultTitle), StringComparer.OrdinalIgnoreCase);
            var hits = expected.Count(t => resultTokens.Contains(t));
            return (double)hits / expected.Count;
        }
    }
}
