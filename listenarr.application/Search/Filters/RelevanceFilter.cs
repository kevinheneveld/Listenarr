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
using Listenarr.Application.Interfaces;
using Listenarr.Domain.Models;

namespace Listenarr.Application.Search.Filters
{
    /// <summary>
    /// Context-aware filter that rejects search results whose title shares too few
    /// significant tokens with the audiobook being searched for. Defends against
    /// indexers returning matter that shares one or two tokens with the query —
    /// e.g., a Patterson Hood concert recording being suggested for a James
    /// Patterson audiobook on the single shared "Patterson" surname.
    ///
    /// Without an audiobook context (manual search, AsinEnricher per-result calls)
    /// the filter fails open — manual users keep seeing every result.
    /// </summary>
    public class RelevanceFilter : ISearchResultFilter
    {
        /// <summary>
        /// Default fraction of significant audiobook tokens that must appear in the
        /// result title for the result to be considered relevant. Empirically chosen
        /// so that single-shared-token false matches (one author surname only) are
        /// rejected while multi-token legitimate matches pass.
        /// </summary>
        public const double DefaultMinRelevance = 0.30;

        public string FilterReason => "title_not_relevant";

        // No audiobook context — fail open. Manual search and per-result calls
        // (AsinEnricher) keep every result.
        public bool ShouldFilter(SearchResult result) => false;

        public bool ShouldFilter(SearchResult result, Audiobook? audiobook)
        {
            if (audiobook == null) return false;
            var relevance = ComputeRelevance(result.Title, audiobook.Title, audiobook.Authors);
            return relevance < DefaultMinRelevance;
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
            foreach (var t in SignificantTokens.From(audiobookTitle)) expected.Add(t);
            if (authors != null)
            {
                foreach (var author in authors)
                {
                    foreach (var t in SignificantTokens.From(author)) expected.Add(t);
                }
            }
            if (expected.Count == 0) return 1.0;

            var resultTokens = new HashSet<string>(SignificantTokens.From(resultTitle), StringComparer.OrdinalIgnoreCase);
            var hits = expected.Count(t => resultTokens.Contains(t));
            return (double)hits / expected.Count;
        }
    }
}
