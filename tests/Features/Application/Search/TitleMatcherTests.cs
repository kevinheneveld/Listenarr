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

namespace Listenarr.Tests.Features.Application.Search
{
    [Trait("Area", "Search")]
    [Trait("Name", "TitleMatcherTests")]
    public class TitleMatcherTests
    {
        // The live case that motivated MostlyMatches: a title+author search
        // for "20,000 Leagues Under the Sea" silently dropped the edition
        // titled "Twenty Thousand Leagues Under the Sea" because strict
        // substring containment can't bridge numeral-vs-word titling.
        [Theory]
        [InlineData("Twenty Thousand Leagues Under the Sea", null, "20,000 Leagues Under the Sea", true)]
        [InlineData("20,000 Leagues Under the Sea", null, "Twenty Thousand Leagues Under the Sea", true)]
        [InlineData("Twenty Thousand Leagues Under the Sea", null, "Twenty Thousand Leagues Under the Sea", true)]
        // A typo'd token drops overlap below the bar (2/5) — the caller's
        // empty-result fallback handles that case instead.
        [InlineData("Twenty Thousand Leagues Under the Sea", null, "20,000 Leages Under the Sea", false)]
        // A different book by the same author must not sneak through.
        [InlineData("The Mysterious Island", null, "20,000 Leagues Under the Sea", false)]
        // Short queries stay strict: one shared word out of two must not
        // admit every book containing it.
        [InlineData("Going Home", null, "Coming Home", false)]
        // Subtitle tokens count toward the overlap.
        [InlineData("Leagues", "Twenty Thousand Under the Sea", "Twenty Thousand Leagues Under the Sea", true)]
        public void MostlyMatches_BridgesEditionTitling(string title, string? subtitle, string query, bool expected)
        {
            Assert.Equal(expected, TitleMatcher.MostlyMatches(title, subtitle, query));
        }

        [Fact]
        public void MostlyMatches_EmptyQuery_MatchesEverything()
        {
            // Matches() treats an empty query as "no filter" — MostlyMatches
            // must agree, not fall through to the token bar.
            Assert.True(TitleMatcher.MostlyMatches("Anything", null, ""));
            Assert.True(TitleMatcher.MostlyMatches("Anything", null, null));
        }
    }
}
