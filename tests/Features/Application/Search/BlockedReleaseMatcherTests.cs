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
using Listenarr.Domain.Search;

namespace Listenarr.Tests.Features.Application.Search
{
    public class BlockedReleaseMatcherTests
    {
        private static List<BlockedRelease> Blocked(params string[] titles) =>
            titles.Select(t => new BlockedRelease { AudiobookId = 1, ReleaseTitle = t }).ToList();

        [Fact]
        public void IsBlocked_ExactTitle_Matches()
        {
            var blocked = Blocked("Jim Butcher - The Dresden Files: Dead Beat [James Marsters, 2010, 82-160 kbps]");

            Assert.True(BlockedReleaseMatcher.IsBlocked(
                "Jim Butcher - The Dresden Files: Dead Beat [James Marsters, 2010, 82-160 kbps]", blocked));
        }

        [Fact]
        public void IsBlocked_PunctuationDrift_StillMatches()
        {
            // Indexers re-list the same release with minor punctuation changes.
            var blocked = Blocked("Neal Stephenson - D (15 book collection) [M4B]");

            Assert.True(BlockedReleaseMatcher.IsBlocked(
                "Neal Stephenson – D: 15 book collection M4B", blocked));
        }

        [Fact]
        public void IsBlocked_DifferentRelease_DoesNotMatch()
        {
            var blocked = Blocked("Neal Stephenson - D (15 book collection) [M4B]");

            Assert.False(BlockedReleaseMatcher.IsBlocked(
                "William R. Forstchen - Five Years After (Unabridged)", blocked));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void IsBlocked_EmptyResultTitle_DoesNotMatch(string? title)
        {
            Assert.False(BlockedReleaseMatcher.IsBlocked(title, Blocked("anything")));
        }

        [Fact]
        public void IsBlocked_NoBlockedEntries_DoesNotMatch()
        {
            Assert.False(BlockedReleaseMatcher.IsBlocked("anything", new List<BlockedRelease>()));
        }
    }
}
