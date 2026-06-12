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
using Listenarr.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Listenarr.Tests.Features.Application.Search
{
    public class SearchResultScorer_RuntimeSizeTests
    {
        private static SearchResultScorer CreateScorer() =>
            new(null, NullLogger<SearchResultScorer>.Instance);

        private static SearchResult Torrent(long sizeBytes) => new()
        {
            Title = "Some Release",
            Size = sizeBytes,
            DownloadType = "torrent",
            Seeders = 50
        };

        private static QualityProfile AnyQuality() => new()
        {
            Name = "Any",
            MinimumSeeders = 0
        };

        [Fact]
        public async Task Score_ReleaseFarLargerThanRuntimeSupports_IsRejected()
        {
            // The live loop: a ~6 GB 15-book collection kept winning the search
            // for a 12h (720 min) book. Ceiling: 720 × 4.8 MB ≈ 3.4 GB.
            var score = await CreateScorer().Score(
                Torrent(6L * 1024 * 1024 * 1024), AnyQuality(), expectedRuntimeMinutes: 720);

            Assert.True(score.TotalScore < 0);
            Assert.Contains(score.RejectionReasons, r => r.Contains("larger than this book's runtime"));
        }

        [Fact]
        public async Task Score_PlausiblySizedRelease_IsNotRejectedBySizeCeiling()
        {
            // A premium-bitrate 12h book (~1.7 GB at 320 kbps) must pass.
            var score = await CreateScorer().Score(
                Torrent(1700L * 1024 * 1024), AnyQuality(), expectedRuntimeMinutes: 720);

            Assert.DoesNotContain(score.RejectionReasons, r => r.Contains("larger than this book's runtime"));
        }

        [Fact]
        public async Task Score_NoRuntimeContext_SkipsTheCeiling()
        {
            var score = await CreateScorer().Score(
                Torrent(50L * 1024 * 1024 * 1024), AnyQuality(), expectedRuntimeMinutes: null);

            Assert.DoesNotContain(score.RejectionReasons, r => r.Contains("larger than this book's runtime"));
        }

        [Fact]
        public async Task Score_ShortBook_KeepsTheMinimumCeilingFloor()
        {
            // A 30-minute short story with a generously mastered 200 MB release
            // must not be rejected (floor is 250 MB).
            var score = await CreateScorer().Score(
                Torrent(200L * 1024 * 1024), AnyQuality(), expectedRuntimeMinutes: 30);

            Assert.DoesNotContain(score.RejectionReasons, r => r.Contains("larger than this book's runtime"));
        }
    }
}
