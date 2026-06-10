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
using System.Text.RegularExpressions;
using Listenarr.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Whisper
{
    /// <summary>
    /// Shell-out wrapper around a bundled whisper.cpp CLI (ADR-0001).
    ///
    /// Packaging is deliberately NOT the FFmpeg runtime-download pattern:
    /// whisper.cpp is MIT and ships no prebuilt Linux binaries (v1.8.6 releases
    /// carry only Windows zips and Apple frameworks), so the Docker image bakes
    /// the binary + ggml model in at build time (see the whisper-builder stage
    /// in the Dockerfile). Dev machines point LISTENARR_WHISPER_BIN /
    /// LISTENARR_WHISPER_MODEL at a local build instead.
    /// </summary>
    public class WhisperService : IWhisperService
    {
        public const string BinaryPathEnvVar = "LISTENARR_WHISPER_BIN";
        public const string ModelPathEnvVar = "LISTENARR_WHISPER_MODEL";

        // base.en transcribes a 90s clip in seconds on a modern CPU, but the
        // target host may be a busy NAS — be generous before declaring a hang.
        private const int TranscribeTimeoutMs = 10 * 60 * 1000;

        private readonly IProcessRunner _processRunner;
        private readonly ILogger<WhisperService> _logger;
        private readonly string _binaryPath;
        private readonly string _modelPath;

        public WhisperService(
            IApplicationPathService applicationPathService,
            IProcessRunner processRunner,
            ILogger<WhisperService> logger)
        {
            _processRunner = processRunner;
            _logger = logger;

            var whisperRoot = Path.Join(applicationPathService.ToolsRootPath, "whisper");
            _binaryPath = Environment.GetEnvironmentVariable(BinaryPathEnvVar)
                          ?? Path.Join(whisperRoot, "whisper-cli");
            _modelPath = Environment.GetEnvironmentVariable(ModelPathEnvVar)
                         ?? Path.Join(whisperRoot, "ggml-base.en.bin");
        }

        public string ModelName
        {
            get
            {
                // "ggml-base.en.bin" → "base.en"; fall back to the bare file name.
                var fileName = Path.GetFileNameWithoutExtension(_modelPath);
                var match = Regex.Match(fileName, @"^ggml-(.+)$");
                return match.Success ? match.Groups[1].Value : fileName;
            }
        }

        public Task<bool> IsAvailableAsync()
        {
            var available = File.Exists(_binaryPath) && File.Exists(_modelPath);
            if (!available)
            {
                _logger.LogInformation(
                    "whisper.cpp unavailable (binary {BinaryExists} at {Binary}, model {ModelExists} at {Model})",
                    File.Exists(_binaryPath), _binaryPath, File.Exists(_modelPath), _modelPath);
            }
            return Task.FromResult(available);
        }

        public async Task<string?> TranscribeAsync(string wavPath, CancellationToken cancellationToken = default)
        {
            if (!await IsAvailableAsync()) return null;
            if (!File.Exists(wavPath))
            {
                _logger.LogWarning("whisper transcription requested for missing clip {Path}", wavPath);
                return null;
            }

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = _binaryPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                startInfo.ArgumentList.Add("-m");
                startInfo.ArgumentList.Add(_modelPath);
                startInfo.ArgumentList.Add("-f");
                startInfo.ArgumentList.Add(wavPath);
                startInfo.ArgumentList.Add("--no-timestamps");
                startInfo.ArgumentList.Add("--no-prints");
                startInfo.ArgumentList.Add("--language");
                startInfo.ArgumentList.Add("en");

                var result = await _processRunner.RunAsync(startInfo, TranscribeTimeoutMs, cancellationToken);
                if (result.TimedOut)
                {
                    _logger.LogWarning("whisper transcription timed out for {Path}", wavPath);
                    return null;
                }
                if (result.ExitCode != 0)
                {
                    _logger.LogWarning(
                        "whisper exited with code {Code} for {Path}: {Stderr}",
                        result.ExitCode, wavPath, Truncate(result.Stderr, 500));
                    return null;
                }

                var transcript = result.Stdout?.Trim();
                return string.IsNullOrWhiteSpace(transcript) ? null : transcript;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "whisper transcription failed for {Path}", wavPath);
                return null;
            }
        }

        private static string Truncate(string? text, int max) =>
            string.IsNullOrEmpty(text) ? string.Empty : (text.Length <= max ? text : text[..max]);
    }
}
