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
using System.Diagnostics;
using Listenarr.Application.Audiobooks.Verification.Contracts;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Ffmpeg.Sampling
{
    /// <summary>
    /// Clips verification sample windows to 16 kHz mono WAV via the bundled
    /// ffmpeg (ADR-0001). The closing window uses ffmpeg's -sseof (seek from
    /// end-of-file) so no duration probe is needed.
    /// </summary>
    public class AudioSampleExtractor : IAudioSampleExtractor
    {
        private const int ClipTimeoutMs = 2 * 60 * 1000;

        private readonly IFfmpegService _ffmpegService;
        private readonly IProcessRunner _processRunner;
        private readonly IConfigurationService _configurationService;
        private readonly ILogger<AudioSampleExtractor> _logger;

        public AudioSampleExtractor(
            IFfmpegService ffmpegService,
            IProcessRunner processRunner,
            IConfigurationService configurationService,
            ILogger<AudioSampleExtractor> logger)
        {
            _ffmpegService = ffmpegService;
            _processRunner = processRunner;
            _configurationService = configurationService;
            _logger = logger;
        }

        public Task<AudioSampleSet> ExtractAsync(string firstFilePath, string? lastFilePath, AudioSampleStrategy strategy, CancellationToken cancellationToken = default)
        {
            var opening = new[] { firstFilePath };
            var closing = new[] { lastFilePath ?? firstFilePath };
            return ExtractAsync(opening, closing, strategy, cancellationToken);
        }

        public async Task<AudioSampleSet> ExtractAsync(IReadOnlyList<string> openingFiles, IReadOnlyList<string> closingFiles, AudioSampleStrategy strategy, CancellationToken cancellationToken = default)
        {
            var ffmpegPath = await _ffmpegService.EnsureFfmpegInstalledAsync();
            if (ffmpegPath == null)
            {
                _logger.LogWarning("Cannot extract verification clips: ffmpeg binary unavailable");
                return new AudioSampleSet();
            }

            var tempDir = Path.Join(Path.GetTempPath(), "listenarr-verification");
            Directory.CreateDirectory(tempDir);

            // Same low-priority treatment as the whisper pass: clipping is short,
            // but a library walk runs it back-to-back for hours.
            var settings = await _configurationService.GetApplicationSettingsAsync();
            var priorityClass = (settings?.VerificationLowCpuPriority ?? true)
                ? ProcessPriorityClass.Idle
                : (ProcessPriorityClass?)null;

            string? opening = null, closing = null;

            if (strategy.OpeningSeconds > 0)
            {
                opening = await ClipAcrossAsync(ffmpegPath, openingFiles, tempDir, fromEnd: false, strategy.OpeningSeconds, priorityClass, cancellationToken);
            }

            if (strategy.ClosingSeconds > 0)
            {
                closing = await ClipAcrossAsync(ffmpegPath, closingFiles, tempDir, fromEnd: true, strategy.ClosingSeconds, priorityClass, cancellationToken);
            }

            return new AudioSampleSet(TryDeleteClip) { OpeningClipPath = opening, ClosingClipPath = closing };
        }

        /// <summary>
        /// Clips one window across consecutive files: forward from the start
        /// for openings, backward accumulating tails for closings, spilling
        /// into the next file until the window's seconds are covered. Pieces
        /// are plain 16 kHz mono s16le WAVs, so multi-file windows are joined
        /// by payload concatenation — no re-encode. A single readable file
        /// short-circuits to the untouched single-clip path.
        /// </summary>
        private async Task<string?> ClipAcrossAsync(
            string ffmpegPath, IReadOnlyList<string> files, string tempDir,
            bool fromEnd, int seconds, ProcessPriorityClass? priorityClass, CancellationToken ct)
        {
            var existing = files.Where(File.Exists).ToList();
            if (existing.Count == 0) return null;
            if (existing.Count == 1)
            {
                return await ClipAsync(ffmpegPath, existing[0], tempDir, fromEnd, seconds, priorityClass, ct);
            }

            var order = fromEnd ? ((IEnumerable<string>)existing).Reverse().ToList() : existing;
            var pieces = new List<string>();
            try
            {
                double remaining = seconds;
                foreach (var file in order)
                {
                    if (remaining < 1) break;
                    var piece = await ClipAsync(ffmpegPath, file, tempDir, fromEnd, (int)Math.Ceiling(remaining), priorityClass, ct);
                    if (piece == null) continue; // unreadable source — try the next
                    pieces.Add(piece);
                    remaining -= WavSeconds(piece);
                }

                if (pieces.Count == 0) return null;
                if (pieces.Count == 1)
                {
                    var only = pieces[0];
                    pieces.Clear(); // keep it out of the finally-cleanup
                    return only;
                }

                if (fromEnd) pieces.Reverse(); // tails were gathered backwards
                var joined = Path.Join(tempDir, $"{Guid.NewGuid():N}.wav");
                ConcatWavs(pieces, joined);
                return joined;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Multi-file clip failed; no window produced");
                return null;
            }
            finally
            {
                foreach (var piece in pieces) TryDeleteClip(piece);
            }
        }

        /// <summary>Seconds of audio in a 16 kHz mono s16le WAV (32,000 bytes/s).</summary>
        internal static double WavSeconds(string wavPath)
        {
            var located = Whisper.WhisperService.TryLocateDataChunk(wavPath);
            return located == null ? 0 : located.Value.DataSize / 32000.0;
        }

        /// <summary>
        /// Joins same-format WAVs by copying the first piece's header and
        /// appending every data payload, RIFF/data sizes patched. Chunk
        /// offsets come from real chunk-walking (ffmpeg inserts LIST/INFO
        /// chunks between fmt and data — assuming the canonical 44-byte
        /// header corrupts the clip; see the whisper head-probe history).
        /// </summary>
        internal static void ConcatWavs(IReadOnlyList<string> pieces, string outputPath)
        {
            var located = new List<(string Path, long Offset, long Size)>();
            foreach (var piece in pieces)
            {
                var loc = Whisper.WhisperService.TryLocateDataChunk(piece);
                if (loc != null && loc.Value.DataSize > 0)
                {
                    located.Add((piece, loc.Value.ChunkOffset, loc.Value.DataSize));
                }
            }
            if (located.Count == 0)
            {
                throw new IOException("No readable WAV pieces to concatenate");
            }

            var totalData = located.Sum(x => x.Size);
            var first = located[0];
            var prefix = new byte[first.Offset + 8];
            using (var head = File.OpenRead(first.Path))
            {
                head.ReadExactly(prefix);
            }
            BitConverter.GetBytes((uint)(first.Offset + totalData)).CopyTo(prefix, 4);
            BitConverter.GetBytes((uint)totalData).CopyTo(prefix, (int)first.Offset + 4);

            using var output = File.Create(outputPath);
            output.Write(prefix);
            var buffer = new byte[81920];
            foreach (var (path, offset, size) in located)
            {
                using var source = File.OpenRead(path);
                source.Seek(offset + 8, SeekOrigin.Begin);
                var left = size;
                while (left > 0)
                {
                    var read = source.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                    if (read <= 0) break;
                    output.Write(buffer, 0, read);
                    left -= read;
                }
            }
        }

        private async Task<string?> ClipAsync(string ffmpegPath, string sourcePath, string tempDir, bool fromEnd, int seconds, ProcessPriorityClass? priorityClass, CancellationToken ct)
        {
            var clipPath = Path.Join(tempDir, $"{Guid.NewGuid():N}.wav");
            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add("-hide_banner");
                startInfo.ArgumentList.Add("-loglevel");
                startInfo.ArgumentList.Add("error");
                if (fromEnd)
                {
                    // Seek relative to end-of-file; on inputs shorter than the
                    // window ffmpeg just starts at 0.
                    startInfo.ArgumentList.Add("-sseof");
                    startInfo.ArgumentList.Add($"-{seconds}");
                }
                startInfo.ArgumentList.Add("-i");
                startInfo.ArgumentList.Add(sourcePath);
                if (!fromEnd)
                {
                    startInfo.ArgumentList.Add("-t");
                    startInfo.ArgumentList.Add(seconds.ToString());
                }
                // whisper.cpp expects 16 kHz mono PCM.
                startInfo.ArgumentList.Add("-vn");
                startInfo.ArgumentList.Add("-ac");
                startInfo.ArgumentList.Add("1");
                startInfo.ArgumentList.Add("-ar");
                startInfo.ArgumentList.Add("16000");
                startInfo.ArgumentList.Add("-c:a");
                startInfo.ArgumentList.Add("pcm_s16le");
                // Without these, ffmpeg copies the source's tags into a
                // LIST/INFO chunk between fmt and data (live case: IART/ICMT
                // pushed the data chunk from byte 44 to 248), which broke the
                // head-probe's WAV handling and skews size-based duration math.
                // Clip metadata is worthless to whisper — always strip it.
                startInfo.ArgumentList.Add("-map_metadata");
                startInfo.ArgumentList.Add("-1");
                startInfo.ArgumentList.Add("-bitexact");
                startInfo.ArgumentList.Add("-y");
                startInfo.ArgumentList.Add(clipPath);

                var result = await _processRunner.RunAsync(startInfo, ClipTimeoutMs, ct, priorityClass);
                if (result.ExitCode != 0 || result.TimedOut || !File.Exists(clipPath))
                {
                    _logger.LogWarning(
                        "ffmpeg clip failed for {Source} ({Window}): exit {Code}, timedOut {TimedOut}",
                        sourcePath, fromEnd ? "closing" : "opening", result.ExitCode, result.TimedOut);
                    TryDelete(clipPath);
                    return null;
                }

                return clipPath;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                TryDelete(clipPath);
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "ffmpeg clip failed for {Source}", sourcePath);
                TryDelete(clipPath);
                return null;
            }
        }

        private static void TryDeleteClip(string? path)
        {
            if (string.IsNullOrEmpty(path)) return;
            TryDelete(path);
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { /* best-effort temp cleanup */ }
        }
    }
}
