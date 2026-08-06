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
using Listenarr.Application.Search.Filters;

namespace Listenarr.Tests.Features.Application.Search
{
    [Trait("Area", "Application")]
    [Trait("Name", "NumberConflictFilterTests")]
    public class NumberConflictFilterTests
    {
        private static bool Filters(string releaseTitle, string bookTitle, string? subtitle = null)
        {
            var filter = new NumberConflictFilter();
            return filter.ShouldFilter(
                new SearchResult { Title = releaseTitle },
                new Audiobook { Title = bookTitle, Subtitle = subtitle });
        }

        [Fact]
        public void EpisodeMismatch_IsRejected()
        {
            // Live case: twelve episode records all grabbing the one
            // "Episode 01" release the indexers carry.
            Assert.True(Filters(
                "Sean Platt and David Wright - Yesterdays Gone - Season 01 Episode 01 (Unabr - 1264k [Various 2011])",
                "Yesterday's Gone: Episode 12"));
        }

        [Fact]
        public void EpisodeMatch_Passes()
        {
            Assert.False(Filters(
                "Sean Platt and David Wright - Yesterdays Gone - Season 01 Episode 01 (Unabr - 1264k [Various 2011])",
                "Yesterday's Gone: Season 1 - Episode 1"));
        }

        [Fact]
        public void SubtitleNumbersCount()
        {
            Assert.True(Filters(
                "Dark Series Book 08 Dark Legend",
                "Dark Dream", "Dark Series, Book 7"));
        }

        [Fact]
        public void ReleaseWithoutNumbers_StaysEligible()
        {
            Assert.False(Filters("Yesterdays Gone Complete Season", "Yesterday's Gone: Episode 12"));
        }

        [Fact]
        public void BookWithoutNumbers_NeverFilters()
        {
            Assert.False(Filters("Some Release Part 3", "The Hobbit"));
        }

        [Theory]
        [InlineData("1984 - George Orwell (2021 remaster) 64k", "1984")]                 // years/bitrate stripped, 1984 kept
        [InlineData("Fahrenheit 451 [1993] 128kbps", "Fahrenheit 451")]
        [InlineData("The 5th Wave 2016 64k", "The 5th Wave")]
        public void YearAndBitrateNoise_DoesNotConflict(string release, string book)
        {
            Assert.False(Filters(release, book));
        }

        [Fact]
        public void YearStrippingIsReleaseSideOnly()
        {
            // A release that REALLY is a different numbered entry still
            // conflicts even when it also carries year noise.
            Assert.True(Filters("Wool 7 of 9 [2012] 64k", "Wool 3"));
        }
    }
}
