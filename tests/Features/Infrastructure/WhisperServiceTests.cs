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
using Listenarr.Infrastructure.Whisper;

namespace Listenarr.Tests.Features.Infrastructure
{
    public class WhisperServiceTests
    {
        [Fact]
        public void NormalizeTranscriptOutput_StripsSegmentTimestamps_AndJoinsSegments()
        {
            // Real whisper-cli timestamped output shape (the production decode runs
            // WITH timestamps because suppressing them makes base.en skip
            // music-overlaid segments such as the spoken credits).
            var stdout =
                "[00:00:00.000 --> 00:00:09.260]   \"This is Audible,\" A Court of Thorns and Roses,\n" +
                "[00:00:09.260 --> 00:00:17.160]   by Sarah J. Mass, narrated by Jennifer Eketa.\n" +
                "[00:00:17.160 --> 00:00:23.520]   Chapter 1 The forest had become a labyrinth of snow\n";

            var transcript = WhisperService.NormalizeTranscriptOutput(stdout);

            Assert.Equal(
                "\"This is Audible,\" A Court of Thorns and Roses, by Sarah J. Mass, narrated by Jennifer Eketa. Chapter 1 The forest had become a labyrinth of snow",
                transcript);
        }

        [Fact]
        public void NormalizeTranscriptOutput_PlainTextWithoutTimestamps_PassesThrough()
        {
            var transcript = WhisperService.NormalizeTranscriptOutput("  Plain text output.  \n");
            Assert.Equal("Plain text output.", transcript);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   \n  \n")]
        public void NormalizeTranscriptOutput_EmptyOutput_ReturnsNull(string? stdout)
        {
            Assert.Null(WhisperService.NormalizeTranscriptOutput(stdout));
        }

        [Fact]
        public void ParseSegments_ReadsTimestampsAndText()
        {
            var stdout =
                "[00:00:00.000 --> 00:00:02.000]   This is Audible.\n" +
                "[00:00:30.000 --> 00:00:35.500]   Daniel glanced over his shoulder.\n";

            var segments = WhisperService.ParseSegments(stdout);

            Assert.Equal(2, segments.Count);
            Assert.Equal(0.0, segments[0].Start);
            Assert.Equal(2.0, segments[0].End);
            Assert.Equal("This is Audible.", segments[0].Text);
            Assert.Equal(30.0, segments[1].Start);
            Assert.Equal(35.5, segments[1].End);
        }

        [Fact]
        public void FindSilentGaps_DetectsTheCreditsHole()
        {
            // The live Artifact Enigma shape: ident at 0–2s, then nothing until
            // the story resumes at the 30s chunk boundary — the 2–30s hole is
            // exactly where the music-overlaid credits live.
            var segments = new List<WhisperService.TranscriptSegment>
            {
                new(0.0, 2.0, "This is Audible."),
                new(30.0, 35.0, "Daniel glanced over his shoulder."),
                new(35.0, 41.0, "A tall leggy blonde gave him a lingering glance."),
            };

            var gaps = WhisperService.FindSilentGaps(segments, clipDurationSeconds: 90.0);

            Assert.Equal(2, gaps.Count);
            Assert.Equal((2.0, 30.0), gaps[0]);
            // Trailing hole (41–90s) also qualifies — closing-window credits sit
            // at the very end of a clip.
            Assert.Equal((41.0, 90.0), gaps[1]);
        }

        [Fact]
        public void FindSilentGaps_IgnoresOrdinaryNarrationPauses()
        {
            var segments = new List<WhisperService.TranscriptSegment>
            {
                new(0.0, 10.0, "a"),
                new(14.0, 25.0, "b"), // 4s pause: scene break, not a credits hole
                new(27.0, 60.0, "c"),
            };

            var gaps = WhisperService.FindSilentGaps(segments, clipDurationSeconds: 62.0);

            Assert.Empty(gaps);
        }

        [Fact]
        public void ShouldProbeHead_FirstSegmentClaimsWholeHead_True()
        {
            // Live case ("Before Eden"): one 0:00–0:28 segment carrying only story
            // text while the announcement sat swallowed in its head.
            var segments = new List<WhisperService.TranscriptSegment>
            {
                new(0.0, 28.3, "I guess, said Jerry Garfield, cutting the engines."),
                new(28.3, 35.2, "The gentle sigh, the underjets faded out."),
            };

            Assert.True(WhisperService.ShouldProbeHead(segments));
        }

        [Fact]
        public void ShouldProbeHead_ShortFirstSegment_False()
        {
            // A first segment that ends inside the head window means the decoder
            // already segmented the head normally — an announcement would be its
            // own segment, nothing to recover.
            var segments = new List<WhisperService.TranscriptSegment>
            {
                new(0.0, 9.2, "Before Eden, by Arthur C. Clarke."),
                new(9.2, 28.0, "I guess, said Jerry Garfield."),
            };

            Assert.False(WhisperService.ShouldProbeHead(segments));
        }

        [Fact]
        public void ShouldProbeHead_LateFirstSegment_False_LeadingGapHandlesIt()
        {
            // First segment starting ≥ the gap threshold is a leading silent hole:
            // FindSilentGaps re-probes 0→start already, head probe must not double up.
            var segments = new List<WhisperService.TranscriptSegment>
            {
                new(12.0, 40.0, "Chapter one."),
            };

            Assert.False(WhisperService.ShouldProbeHead(segments));
            Assert.Contains(WhisperService.FindSilentGaps(segments, 45.0), g => g.Start == 0.0);
        }

        [Fact]
        public void ShouldProbeHead_NoSegments_False()
        {
            Assert.False(WhisperService.ShouldProbeHead(new List<WhisperService.TranscriptSegment>()));
        }

        [Fact]
        public void HeadProbeRecoversNewText_Announcement_NotInFirstSegment_True()
        {
            Assert.True(WhisperService.HeadProbeRecoversNewText(
                "I guess, said Jerry Garfield, cutting the engines, that this is the end of the line.",
                "Before Eden, by Arthur C. Clarke"));
        }

        [Fact]
        public void HeadProbeRecoversNewText_ProbeJustReHearsTheStoryOpening_False()
        {
            // Same words, different punctuation/casing — normalized containment
            // must treat it as already present, not prepend a duplicate.
            Assert.False(WhisperService.HeadProbeRecoversNewText(
                "I guess, said Jerry Garfield, cutting the engines, that this is the end of the line.",
                "\"I guess,\" said Jerry Garfield, cutting the engines"));
        }

        [Fact]
        public void HeadProbeRecoversNewText_TrivialRecovery_False()
        {
            // Decode noise ("[Music]", a stray word) is not a credits announcement.
            Assert.False(WhisperService.HeadProbeRecoversNewText(
                "I guess, said Jerry Garfield.", "[Music]"));
            Assert.False(WhisperService.HeadProbeRecoversNewText(
                "I guess, said Jerry Garfield.", null));
            Assert.False(WhisperService.HeadProbeRecoversNewText(
                "I guess, said Jerry Garfield.", "   "));
        }

        // --- Escalation model name resolution ---------------------------------

        [Theory]
        [InlineData("small.en", "ggml-small.en.bin")]
        [InlineData("  Medium.EN ", "ggml-medium.en.bin")]      // trimmed + lowercased
        [InlineData("base", "ggml-base.bin")]
        public void ModelFileName_ValidNames_MapToGgmlFiles(string name, string expected)
        {
            Assert.Equal(expected, WhisperService.ModelFileName(name));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("../etc/passwd")]                            // path traversal
        [InlineData("small.en/../../x")]
        [InlineData("small en")]                                 // whitespace inside
        [InlineData("-leading-dash")]                            // must start alphanumeric
        public void ModelFileName_InvalidNames_AreRefused(string? name)
        {
            Assert.Null(WhisperService.ModelFileName(name));
        }
    }
}
