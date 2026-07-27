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
using Listenarr.Application.Audiobooks.Verification;

namespace Listenarr.Tests.Features.Application.Audiobooks
{
    [Trait("Area", "Application")]
    [Trait("Name", "TtsSmellDetectorTests")]
    public class TtsSmellDetectorTests
    {
        private static readonly string HolyIslandTranscript =
            "This audio book was compiled by Bipolar Bob specifically for members of the Pirate Bay, " +
            "using technologies developed by Microsoft and Amazon. You can visit Bob's website at " +
            "www.bipolarbob.me. This product has also been published in EPUB format. " +
            "Holy Island A DCI Ryan Mystery by L. J. Ross";

        [Fact]
        public void Score_CompilerAnnouncement_FlagsEvenWhenAgentVerified()
        {
            // The crucial property: TTS rips usually PASS verification, because
            // the synthetic announcement states the right title and author.
            var result = TtsSmellDetector.Score(
                VerificationStatus.AgentVerified,
                HolyIslandTranscript,
                new[] { "Holy Island - part 1.mp3" });

            Assert.True(result.IsCandidate);
            Assert.Contains(result.Reasons, r => r.Contains("compiler-speak"));
            Assert.Contains(result.Reasons, r => r.Contains("technology vendors"));
        }

        [Fact]
        public void Score_ExplicitTtsMention_Flags()
        {
            var result = TtsSmellDetector.Score(
                VerificationStatus.AgentFlagged,
                "This recording was produced using text-to-speech software. Chapter one.",
                Array.Empty<string?>());

            Assert.True(result.IsCandidate);
        }

        [Fact]
        public void Score_TtsTokenInFilePath_Flags()
        {
            var result = TtsSmellDetector.Score(
                VerificationStatus.AgentVerified,
                "Holy Island, a DCI Ryan mystery, by L. J. Ross. Chapter one.",
                new[] { "Holy Island - LJ Ross (TtS Rip)/01.mp3" });

            Assert.True(result.IsCandidate);
        }

        [Fact]
        public void Score_TtsLettersInsideWords_DoNotMatch()
        {
            // "Watts", "attts…" — boundary guard keeps letter runs inside real
            // words from matching the path token.
            var result = TtsSmellDetector.Score(
                VerificationStatus.AgentVerified,
                "The Complete Poems, by Isaac Watts. Read by John Lee.",
                new[] { "Isaac Watts - The Complete Poems/part1.mp3" });

            Assert.False(result.IsCandidate);
        }

        [Fact]
        public void Score_NormalPublisherCredits_NotFlagged()
        {
            var result = TtsSmellDetector.Score(
                VerificationStatus.AgentVerified,
                "Penguin Audio presents Holy Island, by L. J. Ross, read by Jonathan Keeble. Chapter one.",
                new[] { "Holy Island/01 - Chapter 1.mp3" });

            Assert.False(result.IsCandidate);
            Assert.Empty(result.Reasons);
        }

        [Fact]
        public void Score_ForMembersAlone_IsSoftAndCannotFlag()
        {
            // Distribution phrasing describes the release, not the narration —
            // it needs a second signal to cross the threshold.
            var result = TtsSmellDetector.Score(
                VerificationStatus.AgentFlagged,
                "Recorded live for the members of the audio drama society. Act one.",
                Array.Empty<string?>());

            Assert.False(result.IsCandidate);
        }

        [Theory]
        [InlineData(VerificationStatus.ManuallyVerified)]
        [InlineData(VerificationStatus.Rejected)]
        [InlineData(VerificationStatus.Unverified)]
        public void Score_HumanRuledOrUnchecked_NeverCandidates(VerificationStatus status)
        {
            var result = TtsSmellDetector.Score(status, HolyIslandTranscript, new[] { "x (TtS)/1.mp3" });
            Assert.Equal(0, result.Score);
        }
    }
}
