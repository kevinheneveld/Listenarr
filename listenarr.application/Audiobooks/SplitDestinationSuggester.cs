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

namespace Listenarr.Application.Audiobooks
{
    /// <summary>
    /// Suggests the library record a file cluster belongs to: the candidate
    /// whose normalized title is contained in the cluster's name, preferring
    /// the longest title ("Expanded Universe, Vol. 2" beats "Expanded
    /// Universe") and tolerating missing spaces ("TunnelintheSky" matches
    /// "Tunnel in the Sky"). Conservative: no containment, no suggestion —
    /// validated against a live 773-file, 34-book split.
    /// </summary>
    public static class SplitDestinationSuggester
    {
        public static int? Suggest(string clusterDisplayName, IReadOnlyList<(int Id, string Title)> candidates)
        {
            if (string.IsNullOrWhiteSpace(clusterDisplayName) || candidates.Count == 0) return null;

            var name = TitleMatcher.Normalize(clusterDisplayName);
            if (name.Length == 0) return null;
            var nameNoSpace = name.Replace(" ", "");

            int? best = null;
            var bestLength = 0;
            foreach (var (id, title) in candidates)
            {
                var normalized = TitleMatcher.Normalize(title);
                // Very short titles ("Job", "D") match everything; demand 4+ chars.
                if (normalized.Length < 4 || normalized.Length <= bestLength) continue;

                if (name.Contains(normalized, StringComparison.Ordinal)
                    || nameNoSpace.Contains(normalized.Replace(" ", ""), StringComparison.Ordinal))
                {
                    best = id;
                    bestLength = normalized.Length;
                }
            }
            return best;
        }

        /// <summary>
        /// True when the candidate title appears verbatim (normalized, space-
        /// tolerant) inside the cluster name — the strongest evidence the
        /// deterministic matcher produces. An AI refinement must never
        /// override a suggestion this strong (live case: "Book 08 - Dark
        /// Legend" deterministically matched the "Dark Legend" record and the
        /// AI pass overrode it with "Dark Lycan").
        /// </summary>
        public static bool TitleContainedInCluster(string clusterDisplayName, string? title)
        {
            if (string.IsNullOrWhiteSpace(clusterDisplayName) || string.IsNullOrWhiteSpace(title)) return false;
            var name = TitleMatcher.Normalize(clusterDisplayName);
            var normalized = TitleMatcher.Normalize(title);
            if (normalized.Length < 4) return false;
            return name.Contains(normalized, StringComparison.Ordinal)
                || name.Replace(" ", "").Contains(normalized.Replace(" ", ""), StringComparison.Ordinal);
        }

        /// <summary>
        /// Sanity bar for AI-suggested destinations: the cluster name and the
        /// candidate's title must be token-compatible — every meaningful token
        /// of one side present in the other (subtitle counts toward the
        /// candidate side). "Rama" vs "Rendezvous with Rama" passes; "Book 08
        /// - Dark Legend" vs "Dark Lycan" does not (live case: an AI pass
        /// scattered a Dark-series split across Dark Lycan, Shadow Flight,
        /// Shadow Reaper and Leopard's Scar while the right records existed).
        /// </summary>
        public static bool TokensCompatible(string clusterDisplayName, string? candidateTitle, string? candidateSubtitle = null)
        {
            if (string.IsNullOrWhiteSpace(clusterDisplayName) || string.IsNullOrWhiteSpace(candidateTitle)) return false;
            if (TitleContainedInCluster(clusterDisplayName, candidateTitle)) return true;

            var cluster = Tokens(clusterDisplayName);
            if (cluster.Count == 0) return false;

            var titleOnly = Tokens(candidateTitle);
            if (titleOnly.Count > 0 && (titleOnly.IsSubsetOf(cluster) || cluster.IsSubsetOf(titleOnly)))
            {
                return true;
            }
            var withSubtitle = Tokens(candidateTitle + " " + (candidateSubtitle ?? string.Empty));
            return withSubtitle.Count > 0
                && (withSubtitle.IsSubsetOf(cluster) || cluster.IsSubsetOf(withSubtitle));
        }

        private static HashSet<string> Tokens(string text)
        {
            var tokens = new HashSet<string>(StringComparer.Ordinal);
            foreach (var raw in TitleMatcher.Normalize(text).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var token = raw.TrimStart('0');
                if (token.Length == 0) token = "0";
                if (token.Length >= 2 || char.IsDigit(token[0])) tokens.Add(token);
            }
            return tokens;
        }

        /// <summary>
        /// True when the cluster name and a candidate's title/subtitle both
        /// carry numbers but share none. Numbers are the only discriminator
        /// among series siblings, so a suggestion that contradicts the
        /// cluster's number is always wrong — live case: an AI refinement
        /// pass suggested "… Volume 4" for a "… Volume 1" cluster, and the
        /// containment matcher suggested a plain-titled sibling whose
        /// SUBTITLE said "Volume 3". Candidates without any digits stay
        /// eligible — absence of a number is not evidence of the wrong
        /// number. Leading zeros are ignored ("01" matches "1").
        /// </summary>
        public static bool DigitsConflict(string clusterDisplayName, string? candidateTitle, string? candidateSubtitle = null)
        {
            var clusterDigits = DigitTokens(clusterDisplayName);
            if (clusterDigits.Count == 0) return false;
            var candidateDigits = DigitTokens((candidateTitle ?? string.Empty) + " " + (candidateSubtitle ?? string.Empty));
            if (candidateDigits.Count == 0) return false;
            return !clusterDigits.Overlaps(candidateDigits);
        }

        private static HashSet<string> DigitTokens(string text)
        {
            var tokens = new HashSet<string>(StringComparer.Ordinal);
            foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(
                TitleMatcher.Normalize(text), @"\d+"))
            {
                var trimmed = m.Value.TrimStart('0');
                tokens.Add(trimmed.Length == 0 ? "0" : trimmed);
            }
            return tokens;
        }
    }
}
