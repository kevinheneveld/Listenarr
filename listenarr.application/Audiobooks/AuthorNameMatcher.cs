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

namespace Listenarr.Application.Audiobooks
{
    /// <summary>
    /// Author-name normalization and a tolerant "same person?" check shared by the
    /// library repository, the update workflow and the author-ASIN audit.
    /// </summary>
    public static class AuthorNameMatcher
    {
        /// <summary>Letters/digits only, single-spaced, lower-cased — the repository's lookup key.</summary>
        public static string Normalize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var cleaned = new string(value
                .Where(character => char.IsLetterOrDigit(character) || char.IsWhiteSpace(character))
                .ToArray());
            var parts = cleaned.Split(
                new[] { ' ', '\t', '\n', '\r' },
                StringSplitOptions.RemoveEmptyEntries);

            return string.Join(' ', parts).ToLowerInvariant();
        }

        /// <summary>
        /// True when two author strings plausibly name the same person: a trailing
        /// " - role" suffix is ignored ("Jim Butcher - editor"), and any significant
        /// token (3+ chars) in common counts ("Robert A. Heinlein" ↔ "Robert Heinlein").
        /// Different people share nothing ("Fonda Lee" ↔ "James Patterson").
        /// </summary>
        public static bool SharesName(string? a, string? b)
        {
            var ta = Tokens(a);
            var tb = Tokens(b);
            return ta.Count > 0 && tb.Count > 0 && ta.Overlaps(tb);
        }

        /// <summary>True when <paramref name="name"/> shares a name with at least one of <paramref name="candidates"/>.</summary>
        public static bool SharesAnyName(string? name, IEnumerable<string?>? candidates)
        {
            return candidates != null && candidates.Any(candidate => SharesName(name, candidate));
        }

        internal static HashSet<string> Tokens(string? value)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(value))
            {
                return result;
            }

            var roleSeparator = value.IndexOf(" - ", StringComparison.Ordinal);
            var core = roleSeparator > 0 ? value[..roleSeparator] : value;
            foreach (var token in Normalize(core).Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (token.Length >= 3)
                {
                    result.Add(token);
                }
            }

            return result;
        }
    }
}
