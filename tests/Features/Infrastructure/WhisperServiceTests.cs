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
        public void HeadProbeRecoversNewText_AnnouncementSpanningMultipleSegments_False()
        {
            // Live case "Holy Island": the main decode split a long compiled-by
            // announcement across several segments, so first-segment containment
            // failed and the probe text was prepended as a near-verbatim double.
            // Judged against the whole transcript it is nothing new. Note the
            // probe clip ends mid-URL (15s boundary) and spells "BipolarBob"
            // without the space — both must not defeat the match.
            var transcript =
                "This audio book was compiled by Bipolar Bob specifically for members of the Pirate Bay, " +
                "using technologies developed by Microsoft and Amazon. You can visit Bob's website at " +
                "www.bipolarbob.me. This product has also been published in EPUB format for those who " +
                "prefer a more conventional read. Holy Island A DC Iron Mystery by L. J. Ross";
            var probe =
                "This audio book was compiled by BipolarBob specifically for members of the Pirate Bay, " +
                "using technologies developed by Microsoft and Amazon. You can visit Bob's website at www.";

            Assert.False(WhisperService.HeadProbeRecoversNewText(transcript, probe));
        }

        [Fact]
        public void HeadProbeRecoversNewText_SingleVariantWord_False()
        {
            // Two decodes of the same audio differ in odd words; one variant
            // word must not resurrect the duplicate (n-gram coverage, not
            // exact containment).
            var transcript =
                "Penguin Audio presents Escaping Home by A. American, read by Duke Fontaine. " +
                "Chapter one. The morning air was cold.";
            var probe =
                "Penguin Audio presents Escaping Home by Jay American, read by Duke Fontaine.";

            Assert.False(WhisperService.HeadProbeRecoversNewText(transcript, probe));
        }

        [Fact]
        public void HeadProbeRecoversNewText_GenuineRecoveryAgainstFullTranscript_True()
        {
            // The real swallow shape: main decode carries ONLY story text and
            // the probe surfaces an announcement heard nowhere in it.
            var transcript =
                "I guess, said Jerry Garfield, cutting the engines, that this is the end of the line. " +
                "The hovercraft sank slowly onto the rocks.";
            var probe = "Before Eden, by Arthur C. Clarke";

            Assert.True(WhisperService.HeadProbeRecoversNewText(transcript, probe));
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

        // --- Head-probe v2: text gate + physical truncation (live "Before Eden" case) ---

        // The ACTUAL segment shape whisper-cli produced for the 90s "Before Eden"
        // opening sample in production (2026-07-03): the announcement at 0–13s is
        // swallowed into a story-text first segment claiming 0–28.32s.
        private static List<WhisperService.TranscriptSegment> BeforeEden90sSegments() => new()
        {
            new(0.0, 28.32, "I guess, said Jerry Garfield, cutting the engines, that this is the end of the line."),
            new(28.32, 35.34, "The gentle sigh, the underjets faded out. Deprived of its air-cushioned, the scout-car,"),
            new(35.34, 41.76, "rambling wreck, settled down upon the twisted rocks of the Hesperian plateau."),
        };

        [Fact]
        public void BeforeEden90sShape_TriggersBothHeadProbeGates()
        {
            var segments = BeforeEden90sSegments();
            Assert.True(WhisperService.ShouldProbeHead(segments));      // geometry
            Assert.True(WhisperService.HeadLacksCreditText(segments));  // text
        }

        [Fact]
        public void HeadLacksCreditText_AnnouncedOpening_False()
        {
            var segments = new List<WhisperService.TranscriptSegment>
            {
                new(0.0, 6.5, "Blackstone Audio presents All You Zombies, Five Classic Stories by Robert A. Heinlein."),
                new(6.5, 12.0, "This book is read by Spider Robinson."),
            };
            Assert.False(WhisperService.HeadLacksCreditText(segments));
        }

        [Fact]
        public void HeadLacksCreditText_NoSegments_False()
        {
            Assert.False(WhisperService.HeadLacksCreditText(new List<WhisperService.TranscriptSegment>()));
        }

        private static string WriteCanonicalWav(double seconds)
        {
            const int bytesPerSecond = 16000 * 2;
            var dataBytes = (int)(seconds * bytesPerSecond);
            var path = Path.Join(Path.GetTempPath(), $"whisper-test-{Guid.NewGuid():N}.wav");
            var header = new byte[44];
            System.Text.Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
            BitConverter.GetBytes((uint)(36 + dataBytes)).CopyTo(header, 4);
            System.Text.Encoding.ASCII.GetBytes("WAVE").CopyTo(header, 8);
            System.Text.Encoding.ASCII.GetBytes("fmt ").CopyTo(header, 12);
            BitConverter.GetBytes(16u).CopyTo(header, 16);
            BitConverter.GetBytes((ushort)1).CopyTo(header, 20);
            BitConverter.GetBytes((ushort)1).CopyTo(header, 22);
            BitConverter.GetBytes(16000u).CopyTo(header, 24);
            BitConverter.GetBytes((uint)bytesPerSecond).CopyTo(header, 28);
            BitConverter.GetBytes((ushort)2).CopyTo(header, 32);
            BitConverter.GetBytes((ushort)16).CopyTo(header, 34);
            System.Text.Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
            BitConverter.GetBytes((uint)dataBytes).CopyTo(header, 40);
            using var stream = File.Create(path);
            stream.Write(header);
            stream.Write(new byte[dataBytes]);
            return path;
        }

        [Fact]
        public void TryWriteHeadClip_TruncatesCanonicalWav_AndPatchesHeader()
        {
            var source = WriteCanonicalWav(seconds: 30);
            try
            {
                var clip = WhisperService.TryWriteHeadClip(source, seconds: 15);
                Assert.NotNull(clip);
                try
                {
                    const int expectedData = 15 * 16000 * 2;
                    var bytes = File.ReadAllBytes(clip!);
                    Assert.Equal(44 + expectedData, bytes.Length);
                    Assert.Equal((uint)(36 + expectedData), BitConverter.ToUInt32(bytes, 4));
                    Assert.Equal((uint)expectedData, BitConverter.ToUInt32(bytes, 40));
                }
                finally { File.Delete(clip!); }
            }
            finally { File.Delete(source); }
        }

        [Fact]
        public void TryWriteHeadClip_ClipAlreadyShorterThanWindow_Null()
        {
            // The main decode already saw the whole head — a probe would repeat it.
            var source = WriteCanonicalWav(seconds: 10);
            try
            {
                Assert.Null(WhisperService.TryWriteHeadClip(source, seconds: 15));
            }
            finally { File.Delete(source); }
        }

        [Fact]
        public void TryWriteHeadClip_NotACanonicalWav_Null()
        {
            var path = Path.Join(Path.GetTempPath(), $"whisper-test-{Guid.NewGuid():N}.bin");
            File.WriteAllBytes(path, System.Text.Encoding.ASCII.GetBytes(new string('x', 4 * 1024 * 1024)));
            try
            {
                Assert.Null(WhisperService.TryWriteHeadClip(path, seconds: 15));
            }
            finally { File.Delete(path); }
        }

        /// <summary>
        /// The production shape that broke the v2 probe: ffmpeg copies the
        /// source file's tags into a LIST/INFO chunk between fmt and data
        /// (live case "Before Eden": IART "Arthur C Clarke" / ICMT pushed the
        /// data chunk from byte 44 to byte 248). Byte layout mirrors the real
        /// clip captured from the deploy host.
        /// </summary>
        private static string WriteTaggedWav(double seconds)
        {
            const int bytesPerSecond = 16000 * 2;
            var dataBytes = (int)(seconds * bytesPerSecond);
            var path = Path.Join(Path.GetTempPath(), $"whisper-test-{Guid.NewGuid():N}.wav");

            // LIST/INFO payload: IART "Arthur C Clarke\0" (16) + ICMT "www..." — like production.
            var iart = System.Text.Encoding.ASCII.GetBytes("Arthur C Clarke\0");
            var icmt = System.Text.Encoding.ASCII.GetBytes("www.example.org\0");
            var infoBytes = 4 + (8 + iart.Length) + (8 + icmt.Length); // "INFO" + subchunks
            var listChunk = new byte[8 + infoBytes];
            System.Text.Encoding.ASCII.GetBytes("LIST").CopyTo(listChunk, 0);
            BitConverter.GetBytes((uint)infoBytes).CopyTo(listChunk, 4);
            System.Text.Encoding.ASCII.GetBytes("INFO").CopyTo(listChunk, 8);
            System.Text.Encoding.ASCII.GetBytes("IART").CopyTo(listChunk, 12);
            BitConverter.GetBytes((uint)iart.Length).CopyTo(listChunk, 16);
            iart.CopyTo(listChunk, 20);
            System.Text.Encoding.ASCII.GetBytes("ICMT").CopyTo(listChunk, 20 + iart.Length);
            BitConverter.GetBytes((uint)icmt.Length).CopyTo(listChunk, 24 + iart.Length);
            icmt.CopyTo(listChunk, 28 + iart.Length);

            var fmt = new byte[24];
            System.Text.Encoding.ASCII.GetBytes("fmt ").CopyTo(fmt, 0);
            BitConverter.GetBytes(16u).CopyTo(fmt, 4);
            BitConverter.GetBytes((ushort)1).CopyTo(fmt, 8);
            BitConverter.GetBytes((ushort)1).CopyTo(fmt, 10);
            BitConverter.GetBytes(16000u).CopyTo(fmt, 12);
            BitConverter.GetBytes((uint)bytesPerSecond).CopyTo(fmt, 16);
            BitConverter.GetBytes((ushort)2).CopyTo(fmt, 20);
            BitConverter.GetBytes((ushort)16).CopyTo(fmt, 22);

            var riffSize = 4 + fmt.Length + listChunk.Length + 8 + dataBytes;
            using var stream = File.Create(path);
            stream.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            stream.Write(BitConverter.GetBytes((uint)riffSize));
            stream.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
            stream.Write(fmt);
            stream.Write(listChunk);
            stream.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            stream.Write(BitConverter.GetBytes((uint)dataBytes));
            stream.Write(new byte[dataBytes]);
            return path;
        }

        [Fact]
        public void TryLocateDataChunk_TaggedWav_FindsDataPastListChunk()
        {
            var source = WriteTaggedWav(seconds: 30);
            try
            {
                var located = WhisperService.TryLocateDataChunk(source);
                Assert.NotNull(located);
                Assert.True(located!.Value.ChunkOffset > 44); // pushed past the canonical offset
                Assert.Equal(30L * 16000 * 2, located.Value.DataSize);
            }
            finally { File.Delete(source); }
        }

        [Fact]
        public void TryWriteHeadClip_TaggedWav_TruncatesAtRealDataChunk()
        {
            // The v2 regression: canonical-offset math corrupted exactly this shape.
            var source = WriteTaggedWav(seconds: 30);
            try
            {
                var located = WhisperService.TryLocateDataChunk(source)!.Value;
                var clip = WhisperService.TryWriteHeadClip(source, seconds: 15);
                Assert.NotNull(clip);
                try
                {
                    const long expectedData = 15L * 16000 * 2;
                    var bytes = File.ReadAllBytes(clip!);
                    // Same prefix (fmt + LIST survive verbatim), data chunk at the same offset.
                    var clipLocated = WhisperService.TryLocateDataChunk(clip!);
                    Assert.NotNull(clipLocated);
                    Assert.Equal(located.ChunkOffset, clipLocated!.Value.ChunkOffset);
                    Assert.Equal(expectedData, clipLocated.Value.DataSize);
                    Assert.Equal(located.ChunkOffset + 8 + expectedData, bytes.Length);
                    Assert.Equal((uint)(located.ChunkOffset + expectedData), BitConverter.ToUInt32(bytes, 4));
                }
                finally { File.Delete(clip!); }
            }
            finally { File.Delete(source); }
        }

        [Fact]
        public void TryLocateDataChunk_CanonicalWav_DataAt36()
        {
            var source = WriteCanonicalWav(seconds: 5);
            try
            {
                var located = WhisperService.TryLocateDataChunk(source);
                Assert.NotNull(located);
                Assert.Equal(36, located!.Value.ChunkOffset);
                Assert.Equal(5L * 16000 * 2, located.Value.DataSize);
            }
            finally { File.Delete(source); }
        }
    }
}
