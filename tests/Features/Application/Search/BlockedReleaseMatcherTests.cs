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

        // --- Hash matching -------------------------------------------------

        private const string HexHash = "C12FE1C06BBA254A9DC9F519B335AA7C1367A88A";
        // Same 20 bytes, base32-encoded (what many magnet links carry).
        private const string Base32Hash = "YEX6DQDLXISUVHOJ6UM3GNNKPQJWPKEK";

        private static List<BlockedRelease> BlockedWithHash(string title, string? hash) =>
            new() { new BlockedRelease { AudiobookId = 1, ReleaseTitle = title, TorrentHash = hash } };

        [Fact]
        public void IsBlocked_HashMatch_BlocksRetitledRelease()
        {
            // A re-list under a completely different name cannot dodge the blocklist.
            var blocked = BlockedWithHash("Original Release Name [M4B]", HexHash);

            Assert.True(BlockedReleaseMatcher.IsBlocked(
                "Totally Different Title (2026 repack)",
                $"magnet:?xt=urn:btih:{HexHash}&dn=whatever&tr=udp%3A%2F%2Ftracker",
                blocked));
        }

        [Fact]
        public void IsBlocked_Base32Magnet_MatchesHexStoredHash()
        {
            var blocked = BlockedWithHash("Original Release Name", HexHash);

            Assert.True(BlockedReleaseMatcher.IsBlocked(
                "Retitled", $"magnet:?xt=urn:btih:{Base32Hash}", blocked));
        }

        [Fact]
        public void IsBlocked_NoHashOnResult_FallsBackToTitle()
        {
            var blocked = BlockedWithHash("Some Release [MP3]", HexHash);

            Assert.True(BlockedReleaseMatcher.IsBlocked("Some Release MP3", resultMagnetOrHash: null, blocked));
            Assert.False(BlockedReleaseMatcher.IsBlocked("Unrelated Title", resultMagnetOrHash: null, blocked));
        }

        [Fact]
        public void IsBlocked_DifferentHashSameTitle_StillBlocksViaTitleRule()
        {
            var blocked = BlockedWithHash("Some Release [MP3]", HexHash);

            Assert.True(BlockedReleaseMatcher.IsBlocked(
                "Some Release [MP3]",
                "magnet:?xt=urn:btih:0000000000000000000000000000000000000000",
                blocked));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("not-a-hash")]
        [InlineData("magnet:?dn=no-btih-here")]
        [InlineData("ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ00")] // 32 chars, invalid base32 digits for btih
        public void TryExtractInfoHash_Unusable_ReturnsNull(string? input)
        {
            Assert.Null(BlockedReleaseMatcher.TryExtractInfoHash(input));
        }

        [Fact]
        public void TryExtractInfoHash_NormalizesHexAndBase32ToSameValue()
        {
            var fromHex = BlockedReleaseMatcher.TryExtractInfoHash(HexHash.ToLowerInvariant());
            var fromBase32 = BlockedReleaseMatcher.TryExtractInfoHash(Base32Hash);

            Assert.Equal(HexHash, fromHex);
            Assert.Equal(fromHex, fromBase32);
        }
    }
}
