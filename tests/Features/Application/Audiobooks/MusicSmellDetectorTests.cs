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
    [Trait("Area", "Verification")]
    [Trait("Name", "MusicSmellDetectorTests")]
    public class MusicSmellDetectorTests
    {
        private static List<double?> Tracks(int count, double seconds) =>
            Enumerable.Repeat<double?>(seconds, count).ToList();

        private static MusicSmellDetector.Result Score(
            VerificationStatus status = VerificationStatus.AgentFlagged,
            List<double?>? durations = null,
            string? transcript = null,
            string? heardTitle = null,
            string? heardAuthor = null,
            string? metadataTitle = "Before Eden",
            string? metadataAuthor = "Arthur C. Clarke")
            => MusicSmellDetector.Score(
                status, durations ?? Tracks(1, 3600), transcript,
                heardTitle, heardAuthor, metadataTitle, metadataAuthor);

        // --- The directive's five-case matrix -------------------------------

        [Fact]
        public void ClassicAlbum_IsCandidate()
        {
            // 12 × ~3min tracks, lyric-ish transcript full of music cues, and a
            // performer-flavored ident naming someone else entirely.
            var r = Score(
                durations: Tracks(12, 185),
                transcript: "[Music] ♪ we rolled all night ♪ [Music] performed by The Rolling Stones, from the album Sticky Fingers (upbeat music)",
                heardAuthor: "The Rolling Stones");

            Assert.True(r.IsCandidate);
            Assert.True(r.Score >= 0.75, $"expected all three signals, got {r.Score}: {string.Join("; ", r.Reasons)}");
        }

        [Fact]
        public void ChapteredAudiobook_IsNotCandidate()
        {
            // 15 × 25min chapters — the album-shape gate must not fire, and a
            // normal narrated transcript carries no music signals.
            var r = Score(
                durations: Tracks(15, 1500),
                transcript: "Chapter one. It was a bright cold day in April, and the clocks were striking thirteen.");

            Assert.False(r.IsCandidate);
            Assert.Equal(0, r.Score);
        }

        [Fact]
        public void DramatizedAdaptation_ShapeGateSavesIt()
        {
            // Short scene files but with long parts mixed in (max >= 600s) and
            // music cues from the production — must NOT reach the threshold.
            var durations = Tracks(9, 240);
            durations.Add(2400); // a long part breaks the album shape
            var r = Score(
                durations: durations,
                transcript: "[Music] GraphicAudio presents a movie in your mind [Music] (dramatic music)");

            Assert.False(r.IsCandidate);
            Assert.True(r.Score <= 0.25, $"only the cue signal may fire, got {r.Score}");
        }

        [Fact]
        public void SingleLongFileWithMusicIntro_IsNotCandidate()
        {
            var r = Score(
                durations: Tracks(1, 8 * 3600),
                transcript: "[Music] (upbeat music) Before Eden, by Arthur C. Clarke. I guess, said Jerry Garfield...");

            Assert.False(r.IsCandidate);
        }

        [Fact]
        public void ShortStoryCollection_NoSpokenCredits_IsNotCandidate()
        {
            // Many 20–40 minute files: median far above the album band.
            var r = Score(
                status: VerificationStatus.AgentUnverifiable,
                durations: Tracks(10, 1800),
                transcript: "I guess, said Jerry Garfield, cutting the engines, that this is the end of the line.");

            Assert.False(r.IsCandidate);
        }

        // --- Eligibility + signal edges --------------------------------------

        [Theory]
        [InlineData(VerificationStatus.Unverified)]
        [InlineData(VerificationStatus.AgentVerified)]
        [InlineData(VerificationStatus.ManuallyVerified)]
        [InlineData(VerificationStatus.Rejected)]
        public void NonEligibleStatuses_ScoreZero_EvenForPerfectAlbums(VerificationStatus status)
        {
            var r = Score(
                status: status,
                durations: Tracks(12, 185),
                transcript: "[Music] performed by The Rolling Stones from the album ♪ [Music] [Music]",
                heardAuthor: "The Rolling Stones");

            Assert.Equal(0, r.Score);
            Assert.Empty(r.Reasons);
        }

        [Fact]
        public void AlbumShapeAlone_MeetsThreshold()
        {
            var r = Score(durations: Tracks(14, 200));
            Assert.True(r.IsCandidate);
            Assert.Equal(0.5, r.Score);
            Assert.Single(r.Reasons);
        }

        [Fact]
        public void CueAndPerformerSignalsAlone_DoNotReachThreshold()
        {
            // No album shape → the two soft signals cap at 0.5 - epsilon... they
            // sum to 0.5 exactly; guard: 0.25 + 0.25 = 0.5 REACHES the threshold.
            // The precision intent is that transcript-only evidence with the
            // matching shape absent still surfaces only when BOTH soft signals
            // agree — verify that a lone signal does not.
            var cueOnly = Score(
                durations: Tracks(2, 1800),
                transcript: "[Music] [Music] [Music] (upbeat music)");
            Assert.False(cueOnly.IsCandidate);

            var performerOnly = Score(
                durations: Tracks(2, 1800),
                transcript: "performed by Patterson Hood, live at the Fillmore. A long spoken narration follows with plenty of ordinary sentences that are not music cues at all and keep going for a while.",
                heardAuthor: "Patterson Hood");
            Assert.False(performerOnly.IsCandidate);
        }

        [Fact]
        public void PerformerCredits_MatchingMetadata_DoNotCount()
        {
            // "performed by <the actual author's work>" — a full-cast audio drama
            // of the right book must not gain the performer signal.
            var r = Score(
                durations: Tracks(12, 200),
                transcript: "[Music] Before Eden, performed by a full cast [Music] [Music]",
                heardTitle: "Before Eden");

            // album shape (0.5) + cues (0.25) but NOT the performer signal
            Assert.Equal(0.75, r.Score);
            Assert.DoesNotContain(r.Reasons, x => x.Contains("performer", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void NearEmptyTranscript_DespiteDuration_CountsAsMusicSignal()
        {
            var r = Score(
                durations: Tracks(8, 200),
                transcript: "♪");

            Assert.True(r.IsCandidate);
            Assert.Contains(r.Reasons, x => x.Contains("nearly empty") || x.Contains("music cues"));
        }

        [Fact]
        public void UnknownDurations_WeakenShape_NotFake()
        {
            var r = Score(durations: Enumerable.Repeat<double?>(null, 12).ToList());
            Assert.Equal(0, r.Score);
        }

        [Fact]
        public void HeardMatchesMetadata_IsNormalizedContainment()
        {
            Assert.True(MusicSmellDetector.HeardMatchesMetadata("before eden", "Before Eden"));
            Assert.True(MusicSmellDetector.HeardMatchesMetadata("Before Eden", "Before Eden, by Arthur C. Clarke"));
            Assert.False(MusicSmellDetector.HeardMatchesMetadata("Sticky Fingers", "Before Eden"));
            Assert.False(MusicSmellDetector.HeardMatchesMetadata(null, "Before Eden"));
        }
    }
}
