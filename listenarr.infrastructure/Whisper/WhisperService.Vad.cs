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
using Listenarr.Application.Audiobooks.Verification;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Whisper
{
    /// <summary>
    /// Voice-activity-detected re-decode of a sample window that heard nothing
    /// announcement-shaped. whisper.cpp decodes fixed 30 s chunks; when an
    /// announcer speaks over intro music, or a credits block sits right before
    /// the outro, the model routinely emits one segment spanning the whole span
    /// with the speech gone ("CHAPTER I" for 16 s of "Recorded Books, an RB Media
    /// audio brand, presents Frost and Venom by Christine Feehan…"; "She finally
    /// believed." for a 29 s closing credits block). There is no timestamp hole,
    /// so the gap re-probe is blind, and a 15 s head clip is not always enough.
    /// Segmenting on detected speech (Silero VAD, bundled with the binary) gave
    /// the model clips that start at the voice, and it read the credits. The
    /// VAD decode replaces the plain one only when it is the one carrying credit
    /// markers; it is never run when the plain decode already heard credits.
    /// </summary>
    public partial class WhisperService
    {
        public const string VadModelPathEnvVar = "LISTENARR_WHISPER_VAD_MODEL";
        public const string VadModelFileName = "ggml-silero-v5.1.2.bin";
        private const string VadModelDownloadUrl = "https://huggingface.co/ggml-org/whisper-vad/resolve/main/" + VadModelFileName;
        // The Silero model is ~865 KB; anything far below that is an error page.
        private const long MinVadModelBytes = 200L * 1024;

        private int _vadUnavailableLogged;

        /// <summary>
        /// The VAD decode is adopted only when the plain decode heard nothing
        /// credit-shaped and the VAD decode did. Public + pure for unit testing.
        /// </summary>
        public static bool VadDecodeShouldReplace(string? plainTranscript, string? vadTranscript) =>
            !SpokenCreditsExtractor.ContainsCreditMarkers(plainTranscript)
            && SpokenCreditsExtractor.ContainsCreditMarkers(vadTranscript);

        private async Task<string?> TryVadRedecodeAsync(
            string wavPath, string modelPath, string plainTranscript, ProcessPriorityClass? priorityClass, CancellationToken cancellationToken)
        {
            if (SpokenCreditsExtractor.ContainsCreditMarkers(plainTranscript)) return null;

            var vadModelPath = await ResolveVadModelPathAsync(cancellationToken);
            if (vadModelPath == null)
            {
                if (Interlocked.Exchange(ref _vadUnavailableLogged, 1) == 0)
                {
                    _logger.LogInformation(
                        "whisper VAD re-decode unavailable: {File} not found next to the binary, in the models cache, or downloadable — credit-less windows keep the plain decode",
                        VadModelFileName);
                }
                return null;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var stdout = await RunWhisperCliAsync(wavPath, modelPath, offsetMs: null, durationMs: null, priorityClass, cancellationToken, vadModelPath);
            var vadTranscript = NormalizeTranscriptOutput(stdout);
            if (VadDecodeShouldReplace(plainTranscript, vadTranscript))
            {
                _logger.LogInformation(
                    "whisper VAD re-decode: recovered credits ({Chars} chars) for {Path} (plain decode heard none)",
                    vadTranscript!.Length, wavPath);
                return vadTranscript;
            }

            _logger.LogInformation(
                "whisper VAD re-decode: no credits either for {Path} (plain {PlainChars} chars, VAD {VadChars} chars)",
                wavPath, plainTranscript.Length, vadTranscript?.Length ?? 0);
            return null;
        }

        private async Task<string?> ResolveVadModelPathAsync(CancellationToken cancellationToken)
        {
            var overridePath = Environment.GetEnvironmentVariable(VadModelPathEnvVar);
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                return File.Exists(overridePath) ? overridePath : null;
            }

            var bakedDir = Path.GetDirectoryName(_binaryPath);
            var baked = bakedDir == null ? null : Path.Join(bakedDir, VadModelFileName);
            if (baked != null && File.Exists(baked)) return baked;

            var cached = Path.Join(_modelsDownloadDir, VadModelFileName);
            if (File.Exists(cached)) return cached;

            return await DownloadModelAsync(VadModelFileName, cached, VadModelDownloadUrl, MinVadModelBytes, cancellationToken);
        }
    }
}
