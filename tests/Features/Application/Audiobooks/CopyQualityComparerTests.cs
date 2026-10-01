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
    /// <summary>
    /// Fixtures follow the live case: a series pack carrying "A Game of
    /// Thrones" at 128 kbps (33.7 h, 407 chunks) onto a record that held a
    /// 64 kbps copy (33.0 h, 73 files) of the 33.8 h book.
    /// </summary>
    [Trait("Area", "Application")]
    [Trait("Name", "CopyQualityComparerTests")]
    public class CopyQualityComparerTests
    {
        private const double BookSeconds = 33.77 * 3600;

        private static List<AudiobookFile> Copy(int files, double hours, int kbps, string codec = "mp3")
        {
            var seconds = hours * 3600 / files;
            return Enumerable.Range(1, files)
                .Select(i => new AudiobookFile
                {
                    Id = i,
                    DurationSeconds = seconds,
                    Size = (long)(seconds * kbps * 1000 / 8),
                    Bitrate = kbps * 1000,
                    Codec = codec,
                })
                .ToList();
        }

        private static CopyQualityComparer.Comparison Compare(
            List<AudiobookFile> incoming, List<AudiobookFile> existing, double? expectedSeconds = BookSeconds)
            => CopyQualityComparer.Compare(
                CopyQualityComparer.Summarize(incoming),
                CopyQualityComparer.Summarize(existing),
                expectedSeconds,
                CopyQualityComparer.SizesIdentical(incoming, existing));

        [Fact]
        public void Summarize_ReportsEffectiveBitrateAndDominantCodec()
        {
            var summary = CopyQualityComparer.Summarize(Copy(407, 33.7, 128));

            Assert.Equal(407, summary.FileCount);
            Assert.Equal(128, summary.BitrateKbps);
            Assert.Equal("mp3", summary.Codec);
            Assert.True(summary.DurationsComplete);
            Assert.Equal(33.7 * 3600, summary.TotalDurationSeconds, 0);
        }

        [Fact]
        public void Compare_HigherBitrateCompleteCopy_IsBetter()
        {
            var result = Compare(Copy(407, 33.7, 128), Copy(73, 33.0, 64));

            Assert.Equal(CopyQualityComparer.VerdictBetter, result.Verdict);
            Assert.Contains("128 kbps", result.Reason);
            Assert.Contains("64 kbps", result.Reason);
        }

        [Fact]
        public void Compare_LowerBitrateCopy_IsWorse()
        {
            var result = Compare(Copy(394, 33.7, 16), Copy(73, 33.0, 64));

            Assert.Equal(CopyQualityComparer.VerdictWorse, result.Verdict);
        }

        [Fact]
        public void Compare_BitratesWithinTwentyPercent_AreSimilar()
        {
            var result = Compare(Copy(40, 33.7, 70), Copy(73, 33.0, 64));

            Assert.Equal(CopyQualityComparer.VerdictSimilar, result.Verdict);
        }

        [Fact]
        public void Compare_EmptyDestination_HasNothingToCompare()
        {
            var result = Compare(Copy(407, 33.7, 128), new List<AudiobookFile>());

            Assert.Equal(CopyQualityComparer.VerdictEmpty, result.Verdict);
        }

        [Fact]
        public void Compare_SameFileSizes_IsTheSameDownloadAgain()
        {
            // Live case: the pack's 16 kbps books were byte-for-byte what two
            // other records already held from an earlier import of the pack.
            var result = Compare(Copy(394, 37.2, 16), Copy(394, 37.2, 16), 37.2 * 3600);

            Assert.Equal(CopyQualityComparer.VerdictIdentical, result.Verdict);
        }

        [Fact]
        public void Compare_CompleteCopyBeatsPartialOne_EvenAtLowerBitrate()
        {
            var result = Compare(Copy(100, 33.7, 64), Copy(20, 12.0, 128));

            Assert.Equal(CopyQualityComparer.VerdictBetter, result.Verdict);
            Assert.Contains("whole book", result.Reason);
        }

        [Fact]
        public void Compare_PartialCopy_IsWorseThanACompleteOne()
        {
            var result = Compare(Copy(20, 12.0, 128), Copy(100, 33.7, 64));

            Assert.Equal(CopyQualityComparer.VerdictWorse, result.Verdict);
        }

        [Fact]
        public void Compare_GroupFarLongerThanTheBook_IsNotThisBook()
        {
            // An unsplit 85 h blob (two books) offered to a 47.6 h record must
            // never read as "better quality".
            var result = Compare(Copy(952, 84.8, 128), Copy(558, 47.6, 16), 47.57 * 3600);

            Assert.Equal(CopyQualityComparer.VerdictDifferent, result.Verdict);
        }

        [Fact]
        public void Compare_NoCatalogRuntime_DisagreeingLengths_AreDifferent()
        {
            var result = Compare(Copy(40, 10.0, 128), Copy(73, 33.0, 64), expectedSeconds: null);

            Assert.Equal(CopyQualityComparer.VerdictDifferent, result.Verdict);
        }

        [Fact]
        public void Compare_MissingDurations_IsUnknown()
        {
            var incoming = Copy(10, 33.7, 128);
            incoming[3].DurationSeconds = null;

            var result = Compare(incoming, Copy(73, 33.0, 64));

            Assert.Equal(CopyQualityComparer.VerdictUnknown, result.Verdict);
        }

        [Fact]
        public void Compare_DifferentCodecs_SaysBitrateIsOnlyARoughGuide()
        {
            var result = Compare(Copy(49, 33.0, 64), Copy(1, 33.9, 96, codec: "aac"));

            Assert.Equal(CopyQualityComparer.VerdictWorse, result.Verdict);
            Assert.Contains("rough guide", result.Reason);
        }
    }
}
