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
using Listenarr.Infrastructure.Ffmpeg.Sampling;
using Listenarr.Infrastructure.Whisper;

namespace Listenarr.Tests.Features.Infrastructure.Ffmpeg
{
    /// <summary>
    /// Pure WAV plumbing behind multi-file sample windows: pieces are the
    /// 16 kHz mono s16le WAVs ffmpeg produces, joined by payload
    /// concatenation with patched RIFF sizes.
    /// </summary>
    [Trait("Area", "Verification")]
    [Trait("Name", "AudioSampleExtractorWavTests")]
    public class AudioSampleExtractorWavTests : IDisposable
    {
        private readonly string _dir = Directory.CreateTempSubdirectory("wav-concat-tests").FullName;

        public void Dispose()
        {
            try { Directory.Delete(_dir, true); } catch (IOException) { }
        }

        /// <summary>Minimal canonical 16kHz mono s16le WAV with the given payload.</summary>
        private string WriteWav(string name, byte[] payload)
        {
            var path = Path.Join(_dir, name);
            using var fs = File.Create(path);
            using var bw = new BinaryWriter(fs);
            bw.Write("RIFF"u8); bw.Write((uint)(36 + payload.Length)); bw.Write("WAVE"u8);
            bw.Write("fmt "u8); bw.Write(16u); bw.Write((ushort)1); bw.Write((ushort)1);
            bw.Write(16000u); bw.Write(32000u); bw.Write((ushort)2); bw.Write((ushort)16);
            bw.Write("data"u8); bw.Write((uint)payload.Length); bw.Write(payload);
            return path;
        }

        [Fact]
        public void ConcatWavs_JoinsPayloadsInOrder_WithPatchedSizes()
        {
            var a = WriteWav("a.wav", new byte[] { 1, 1, 1, 1 });
            var b = WriteWav("b.wav", new byte[] { 2, 2 });
            var c = WriteWav("c.wav", new byte[] { 3, 3, 3, 3, 3, 3 });
            var output = Path.Join(_dir, "joined.wav");

            AudioSampleExtractor.ConcatWavs(new[] { a, b, c }, output);

            var located = WhisperService.TryLocateDataChunk(output);
            Assert.NotNull(located);
            Assert.Equal(12, located!.Value.DataSize);

            var bytes = File.ReadAllBytes(output);
            var payload = bytes[(int)(located.Value.ChunkOffset + 8)..];
            Assert.Equal(new byte[] { 1, 1, 1, 1, 2, 2, 3, 3, 3, 3, 3, 3 }, payload);
        }

        [Fact]
        public void WavSeconds_UsesPayloadLength()
        {
            // 32,000 bytes per second at 16 kHz mono s16le.
            var w = WriteWav("secs.wav", new byte[64000]);
            Assert.Equal(2.0, AudioSampleExtractor.WavSeconds(w), 3);
        }

        [Fact]
        public void ConcatWavs_AllPiecesUnreadable_Throws()
        {
            var bogus = Path.Join(_dir, "bogus.wav");
            File.WriteAllText(bogus, "not a wav");
            Assert.Throws<IOException>(() =>
                AudioSampleExtractor.ConcatWavs(new[] { bogus }, Path.Join(_dir, "out.wav")));
        }
    }
}
