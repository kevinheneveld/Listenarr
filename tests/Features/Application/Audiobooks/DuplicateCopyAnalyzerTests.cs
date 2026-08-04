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
            long[]? sizes = null,
            string? displayName = null)
        {
            sizes ??= Enumerable.Repeat(totalBytes / fileCount, fileCount).ToArray();
            return new DuplicateCopyAnalyzer.ClusterEvidence(
                key,
                displayName ?? key,
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

            var result = DuplicateCopyAnalyzer.Analyze([standalone, pair]);

            var p = Assert.Single(result.Proposals);
            Assert.Equal("stem:brother-a", p.Keeper.Key);
            Assert.Equal(DuplicateCopyAnalyzer.ConfidenceHigh, p.Confidence);
            Assert.Equal(185_000_000, p.ReclaimableBytes);
            Assert.False(result.SplitCandidate);
        }

        [Fact]
        public void EqualBitrate_KeeperIsMoreChapterized()
        {
            var single = Cluster("stem:a", 8.0, 200_000_000, 1);
            var chapterized = Cluster("stem:b", 8.0, 200_000_000, 24);

            var p = Assert.Single(DuplicateCopyAnalyzer.Analyze([single, chapterized]).Proposals);
            Assert.Equal("stem:b", p.Keeper.Key);
        }

        [Fact]
        public void DurationsDivergePastLooseBand_NothingReported()
        {
            // 8.6h vs 11.6h: different books sharing a record — must not pair,
            // and this far apart is not even split kinship by runtime alone.
            var a = Cluster("stem:a", 8.6, 200_000_000, 1);
            var b = Cluster("stem:b", 11.6, 300_000_000, 2);

            var result = DuplicateCopyAnalyzer.Analyze([a, b]);
            Assert.Empty(result.Proposals);
            Assert.False(result.SplitCandidate);
        }

        [Fact]
        public void LooseDurationAgreement_IsSplitCandidateNotProposal()
        {
            // First live run: the 75–98% band is where different books of one
            // series masquerade as copies (Foundation, Dark Dream). That shape
            // now signals "split this record" and never proposes deletions.
            var a = Cluster("stem:a", 8.0, 200_000_000, 10);
            var b = Cluster("stem:b", 9.5, 240_000_000, 12);

            var result = DuplicateCopyAnalyzer.Analyze([a, b]);
            Assert.Empty(result.Proposals);
            Assert.True(result.SplitCandidate);
        }

        [Fact]
        public void ConflictingNumbersInNames_NeverPair_EvenWithMatchingRuntimes()
        {
            // Sibling volumes can have near-equal runtimes (Odd Apocalypse's
            // 5.4h halves); the number in the name is the tiebreaker.
            var a = Cluster("stem:a", 12.0, 300_000_000, 10,
                displayName: "Dark - Book 008 - Dark Legend");
            var b = Cluster("stem:b", 12.0, 310_000_000, 10,
                displayName: "Dark - Book 009 - Dark Guardian");

            var result = DuplicateCopyAnalyzer.Analyze([a, b]);
            Assert.Empty(result.Proposals);
            Assert.True(result.SplitCandidate);
        }

        [Fact]
        public void ByteIdenticalClusters_PairDespiteConflictingNames()
        {
            // A mislabeled copy is still a copy: identical size multisets win
            // over whatever the folder names claim.
            var sizes = Enumerable.Range(1, 20).Select(i => 10_000_000L + i).ToArray();
            var a = Cluster("stem:a", 10.0, sizes.Sum(), 20, sizes: sizes,
                displayName: "Book 1");
            var b = Cluster("stem:b", 10.0, sizes.Sum(), 20, sizes: sizes,
                displayName: "Book 2");

            var result = DuplicateCopyAnalyzer.Analyze([a, b]);
            var p = Assert.Single(result.Proposals);
            Assert.True(p.SizesIdentical);
        }

        [Fact]
        public void MissingDurations_MatchOnlyOnExactSizeMultiset()
        {
            var sizes = new long[] { 27_000_000, 28_000_000, 29_000_000 };
            var a = Cluster("stem:a", 0, 84_000_000, 3, durationsComplete: false, sizes: sizes);
            var b = Cluster("stem:b", 0, 84_000_000, 3, durationsComplete: false, sizes: sizes);
            var c = Cluster("stem:c", 0, 84_000_000, 3, durationsComplete: false,
                sizes: [27_000_000, 28_000_000, 29_000_001]);

            var result = DuplicateCopyAnalyzer.Analyze([a, b, c]);

            var p = Assert.Single(result.Proposals);
            Assert.True(p.SizesIdentical);
            Assert.DoesNotContain(p.Redundant, r => r.Key == "stem:c");
        }

        [Fact]
        public void ShortClusters_NeverParticipate()
        {
            // First live run: 189 twelve-minute "Part NNN of" stubs with
            // agreeing durations united into one keep-one-drop-188 proposal.
            // Anything under 30 minutes proves nothing about book identity.
            var a = Cluster("stem:a", 0.2, 6_000_000, 1);
            var b = Cluster("stem:b", 0.2, 6_000_000, 1);
            var c = Cluster("stem:c", 0.2, 6_000_000, 1);

            var result = DuplicateCopyAnalyzer.Analyze([a, b, c]);
            Assert.Empty(result.Proposals);
            Assert.False(result.SplitCandidate);
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

            var p = Assert.Single(DuplicateCopyAnalyzer.Analyze([a, b, c]).Proposals);
            Assert.Equal(2, p.Redundant.Count);
            Assert.True(p.SizesIdentical);
        }
    }
}
