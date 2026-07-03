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

namespace Listenarr.Infrastructure.Whisper
{
    /// <summary>
    /// Head re-probe helpers: recovering a spoken announcement whisper swallowed
    /// into the opening decode chunk (see the head re-probe block in
    /// TranscribeCoreAsync for the full mechanics and the live case).
    /// </summary>
    public partial class WhisperService
    {
        // The head window a swallowed announcement fits in. Deliberately half a
        // whisper decode chunk: long enough for "Title, by Author" plus the pause
        // that follows, short enough that the isolated decode can't itself merge
        // the announcement into story text.
        private const double HeadProbeSeconds = 15.0;

        // Below this many normalized characters a head recovery is decode noise
        // ("[Music]", a stray word), not a credits announcement.
        private const int MinHeadRecoveryChars = 10;

        /// <summary>
        /// True when the clip's first segment claims the whole head of the clip
        /// (starts near 0, extends past <see cref="HeadProbeSeconds"/>) — the
        /// shape under which whisper can swallow a short spoken announcement into
        /// the opening chunk. A first segment that starts LATE is a leading gap
        /// and is already handled by <see cref="FindSilentGaps"/>. Public + pure
        /// for unit testing.
        /// </summary>
        public static bool ShouldProbeHead(IReadOnlyList<TranscriptSegment> segments)
        {
            if (segments.Count == 0) return false;
            var first = segments.OrderBy(s => s.Start).First();
            return first.Start < MinGapSeconds && first.End >= HeadProbeSeconds;
        }

        /// <summary>
        /// True when the opening text of the clip (first two segments) carries no
        /// announcement-shaped content ("… by …", "narrated by", "presents", …)
        /// — the audio may still open with an announcement whisper swallowed even
        /// when the segment geometry looks innocent, so such clips get a head
        /// probe too. Public + pure for unit testing.
        /// </summary>
        public static bool HeadLacksCreditText(IReadOnlyList<TranscriptSegment> segments)
        {
            if (segments.Count == 0) return false;
            var headText = string.Join(" ",
                segments.OrderBy(s => s.Start).Take(2).Select(s => s.Text));
            return !Listenarr.Application.Audiobooks.Verification.SpokenCreditsExtractor
                .ContainsCreditMarkers(headText);
        }

        /// <summary>
        /// Writes the first <paramref name="seconds"/> of a canonical
        /// verification clip (44-byte-header 16 kHz mono s16le WAV) to a sibling
        /// temp file, patching the RIFF/data sizes. Returns null when the input
        /// is not such a WAV, is already shorter than the window, or on IO
        /// failure — the caller simply skips the probe. Physical truncation is
        /// load-bearing: whisper-cli's --duration does not shorten the decode
        /// window (see the head re-probe comment).
        /// </summary>
        public static string? TryWriteHeadClip(string wavPath, double seconds)
        {
            const int headerBytes = 44;
            const int bytesPerSecond = 16000 * 2; // 16 kHz, mono, 16-bit
            try
            {
                var info = new FileInfo(wavPath);
                var wantedDataBytes = (long)(seconds * bytesPerSecond);
                // Already short enough → the main decode saw the whole head; a
                // probe would just repeat it.
                if (info.Length <= headerBytes + wantedDataBytes) return null;

                var clipPath = wavPath + ".head.wav";
                using var source = File.OpenRead(wavPath);
                var header = new byte[headerBytes];
                if (source.Read(header, 0, headerBytes) != headerBytes) return null;
                // Not a plain PCM WAV with the canonical 44-byte header — bail
                // rather than produce a corrupt clip.
                if (header[0] != (byte)'R' || header[1] != (byte)'I' || header[2] != (byte)'F' || header[3] != (byte)'F'
                    || header[12] != (byte)'f' || header[13] != (byte)'m' || header[14] != (byte)'t')
                {
                    return null;
                }

                BitConverter.GetBytes((uint)(headerBytes - 8 + wantedDataBytes)).CopyTo(header, 4);
                BitConverter.GetBytes((uint)wantedDataBytes).CopyTo(header, 40);

                using var dest = File.Create(clipPath);
                dest.Write(header, 0, headerBytes);
                var remaining = wantedDataBytes;
                var buffer = new byte[81920];
                while (remaining > 0)
                {
                    var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                    if (read <= 0) break;
                    dest.Write(buffer, 0, read);
                    remaining -= read;
                }
                return clipPath;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return null;
            }
        }

        /// <summary>
        /// True when the head probe surfaced text worth prepending: non-trivial
        /// after normalization and not already contained (normalized) in the
        /// first segment — a probe that just re-hears the story opening must not
        /// duplicate it. Public + pure for unit testing.
        /// </summary>
        public static bool HeadProbeRecoversNewText(string firstSegmentText, string? probeText)
        {
            var probe = NormalizeForComparison(probeText);
            if (probe.Length < MinHeadRecoveryChars) return false;
            var first = NormalizeForComparison(firstSegmentText);
            return !first.Contains(probe, StringComparison.Ordinal);
        }

        // Lowercased letters/digits only (punctuation and whitespace removed) —
        // both differ freely between two decodes of the same audio.
        private static string NormalizeForComparison(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;
            var chars = text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray();
            return new string(chars);
        }
    }
}
