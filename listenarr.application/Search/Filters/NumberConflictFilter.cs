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

namespace Listenarr.Application.Search.Filters;

/// <summary>
/// Context-aware filter that rejects results whose NUMBER contradicts the
/// audiobook's. Numbers are the only discriminator among serialized siblings:
/// live case, a library tracking twelve "Yesterday's Gone" episode records
/// against an indexer offering only "Season 01 Episode 01" — every episode's
/// search grabbed Episode 01 (eight grabs in one sweep, several importing the
/// wrong audio). Same rule as the split picker's digit guard: both sides must
/// carry numbers to conflict — a release without numbers stays eligible, and
/// release-side noise (4-digit years, bitrate tokens like "64k"/"1264kbps")
/// is stripped before comparing so "[2011]" never counts as a number.
/// Without an audiobook context the filter fails open.
/// </summary>
public class NumberConflictFilter : ISearchResultFilter
{
    public string FilterReason => "number_conflict";

    // Years and bitrate-ish tokens carry no book identity. Years are stripped
    // ONLY from the release side — "1984" the record title must keep its digits.
    private static readonly Regex ReleaseNoiseRegex = new(
        @"\b(19|20)\d{2}\b|\b\d+\s*k(bps|bit|b)?\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DigitRunRegex = new(@"\d+", RegexOptions.Compiled);

    public bool ShouldFilter(SearchResult result) => false;

    public bool ShouldFilter(SearchResult result, Audiobook? audiobook)
    {
        if (audiobook == null) return false;

        var bookDigits = DigitTokens(
            (audiobook.Title ?? string.Empty) + " " + (audiobook.Subtitle ?? string.Empty));
        if (bookDigits.Count == 0) return false;

        var releaseDigits = DigitTokens(
            ReleaseNoiseRegex.Replace(result.Title ?? string.Empty, " "));
        if (releaseDigits.Count == 0) return false;

        return !bookDigits.Overlaps(releaseDigits);
    }

    private static HashSet<string> DigitTokens(string text)
    {
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in DigitRunRegex.Matches(TitleMatcher.Normalize(text)))
        {
            var trimmed = m.Value.TrimStart('0');
            tokens.Add(trimmed.Length == 0 ? "0" : trimmed);
        }
        return tokens;
    }
}
