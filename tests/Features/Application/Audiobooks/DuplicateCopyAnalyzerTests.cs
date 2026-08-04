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
using Listenarr.Application.Audiobooks;

namespace Listenarr.Tests.Features.Application.Audiobooks
{
    [Trait("Area", "Application")]
    [Trait("Name", "DuplicateCopyAnalyzerTests")]
    public class DuplicateCopyAnalyzerTests
    {
        private static int _nextFileId = 1;

        private static DuplicateCopyAnalyzer.ClusterEvidence Cluster(
            string key,
            double durationHours,
            long totalBytes,
            int fileCount,
            bool durationsComplete = true,
            long[]? sizes = null)
        {
            sizes ??= Enumerable.Repeat(totalBytes / fileCount, fileCount).ToArray();
            return new DuplicateCopyAnalyzer.ClusterEvidence(
                key,
                key,
                Enumerable.Range(_nextFileId += fileCount, fileCount).ToList(),
                fileCount,
                totalBytes,
                durationHours * 3600,
                durationsComplete,
                sizes.OrderBy(s => s).ToList());
        }

        [Fact]
        public void TwoRipsOfSameBook_KeeperIsHigherBitrate()
        {
            // Live case (Brother Odd): a 9.4h standalone at 272MB vs a 9.4h
            // two-file pair at 185MB — same audio, keep the better encode.
            var standalone = Cluster("stem:brother-a", 9.4, 272_000_000, 1);
            var pair = Cluster("stem:brother-b", 9.4, 185_000_000, 2);

            var proposals = DuplicateCopyAnalyzer.Analyze([standalone, pair]);

            var p = Assert.Single(proposals);
            Assert.Equal("stem:brother-a", p.Keeper.Key);
            Assert.Equal(DuplicateCopyAnalyzer.ConfidenceHigh, p.Confidence);
            Assert.Equal(185_000_000, p.ReclaimableBytes);
        }

        [Fact]
        public void EqualBitrate_KeeperIsMoreChapterized()
        {
            var single = Cluster("stem:a", 8.0, 200_000_000, 1);
            var chapterized = Cluster("stem:b", 8.0, 200_000_000, 24);

            var p = Assert.Single(DuplicateCopyAnalyzer.Analyze([single, chapterized]));
            Assert.Equal("stem:b", p.Keeper.Key);
        }

        [Fact]
        public void DurationsDivergePastTolerance_NoProposal()
        {
            // 8.6h vs 11.6h: different books sharing a record — must not pair.
            var a = Cluster("stem:a", 8.6, 200_000_000, 1);
            var b = Cluster("stem:b", 11.6, 300_000_000, 2);

            Assert.Empty(DuplicateCopyAnalyzer.Analyze([a, b]));
        }

        [Fact]
        public void LooseDurationAgreement_IsReviewNotHigh()
        {
            // Within the detector's 75% band but not tight — plausibly the
            // same book (abridged vs not?) — surfaced for a human, never "high".
            var a = Cluster("stem:a", 8.0, 200_000_000, 10);
            var b = Cluster("stem:b", 9.5, 240_000_000, 12);

            var p = Assert.Single(DuplicateCopyAnalyzer.Analyze([a, b]));
            Assert.Equal(DuplicateCopyAnalyzer.ConfidenceReview, p.Confidence);
        }

        [Fact]
        public void MissingDurations_MatchOnlyOnExactSizeMultiset()
        {
            var sizes = new long[] { 7_000_000, 8_000_000, 9_000_000 };
            var a = Cluster("stem:a", 0, 24_000_000, 3, durationsComplete: false, sizes: sizes);
            var b = Cluster("stem:b", 0, 24_000_000, 3, durationsComplete: false, sizes: sizes);
            var c = Cluster("stem:c", 0, 24_000_000, 3, durationsComplete: false,
                sizes: [7_000_000, 8_000_000, 9_000_001]);

            var proposals = DuplicateCopyAnalyzer.Analyze([a, b, c]);

            var p = Assert.Single(proposals);
            Assert.True(p.SizesIdentical);
            Assert.DoesNotContain(p.Redundant, r => r.Key == "stem:c");
        }

        [Fact]
        public void TinyClusters_NeverParticipate()
        {
            // Two 4-minute stubs with agreeing durations prove nothing.
            var a = Cluster("stem:a", 0.07, 2_000_000, 1);
            var b = Cluster("stem:b", 0.07, 2_000_000, 1);

            Assert.Empty(DuplicateCopyAnalyzer.Analyze([a, b]));
        }

        [Fact]
        public void ThreeCopies_CollapseIntoOneProposal()
        {
            // Live case (20,000 Leagues): three identical 989MB sets — one
            // proposal, one keeper, two redundant.
            var sizes = Enumerable.Range(1, 46).Select(i => 20_000_000L + i).ToArray();
            var a = Cluster("stem:a", 10.0, sizes.Sum(), 46, sizes: sizes);
            var b = Cluster("stem:b", 10.0, sizes.Sum(), 46, sizes: sizes);
            var c = Cluster("stem:c", 10.0, sizes.Sum(), 46, sizes: sizes);

            var p = Assert.Single(DuplicateCopyAnalyzer.Analyze([a, b, c]));
            Assert.Equal(2, p.Redundant.Count);
            Assert.True(p.SizesIdentical);
        }
    }
}
