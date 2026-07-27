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
using Listenarr.Application.Audiobooks.Verification.Contracts;
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
    public partial class WhisperService : IWhisperService
    {
        public const string BinaryPathEnvVar = "LISTENARR_WHISPER_BIN";
        public const string ModelPathEnvVar = "LISTENARR_WHISPER_MODEL";

        // base.en transcribes a 90s clip in seconds on a modern CPU, but the
        // target host may be a busy NAS — be generous before declaring a hang.
        private const int TranscribeTimeoutMs = 10 * 60 * 1000;

        // Escalation models are fetched at runtime (the image bakes only the
        // first-pass model). ggml-small.en.bin is ~466 MB; anything shorter than
        // this is a truncated/failed download, never a real model.
        private const long MinDownloadedModelBytes = 100L * 1024 * 1024;
        private const string ModelDownloadUrlTemplate =
            "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{0}";

        // Single-flight for model downloads: concurrent escalations must not
        // race a 466 MB fetch. Static — the service is scoped, the cache is not.
        private static readonly SemaphoreSlim ModelDownloadLock = new(1, 1);

        private readonly IProcessRunner _processRunner;
        private readonly IConfigurationService _configurationService;
        private readonly IHttpClientFactory? _httpClientFactory;
        private readonly ILogger<WhisperService> _logger;
        private readonly string _binaryPath;
        private readonly string _modelPath;
        private readonly string _modelsDownloadDir;

        public WhisperService(
            IApplicationPathService applicationPathService,
            IProcessRunner processRunner,
            IConfigurationService configurationService,
            ILogger<WhisperService> logger,
            IHttpClientFactory? httpClientFactory = null)
        {
            _processRunner = processRunner;
            _configurationService = configurationService;
            _logger = logger;
            _httpClientFactory = httpClientFactory;

            var whisperRoot = Path.Join(applicationPathService.ToolsRootPath, "whisper");
            _binaryPath = Environment.GetEnvironmentVariable(BinaryPathEnvVar)
                          ?? Path.Join(whisperRoot, "whisper-cli");
            _modelPath = Environment.GetEnvironmentVariable(ModelPathEnvVar)
                         ?? Path.Join(whisperRoot, "ggml-base.en.bin");
            // Escalation models live on the persistent config volume (like the
            // ffmpeg runtime download), so an image swap doesn't re-download 466 MB.
            _modelsDownloadDir = Path.Join(applicationPathService.ConfigRootPath, "whisper-models");
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

        public Task<string?> TranscribeAsync(string wavPath, CancellationToken cancellationToken = default)
            => TranscribeCoreAsync(wavPath, _modelPath, cancellationToken);

        public async Task<string?> TranscribeWithModelAsync(string wavPath, string modelName, CancellationToken cancellationToken = default)
        {
            var modelPath = await ResolveModelPathAsync(modelName, cancellationToken);
            if (modelPath == null) return null;
            return await TranscribeCoreAsync(wavPath, modelPath, cancellationToken);
        }

        private async Task<string?> TranscribeCoreAsync(string wavPath, string modelPath, CancellationToken cancellationToken)
        {
            if (!File.Exists(_binaryPath) || !File.Exists(modelPath))
            {
                _logger.LogInformation(
                    "whisper.cpp unavailable (binary {BinaryExists} at {Binary}, model {ModelExists} at {Model})",
                    File.Exists(_binaryPath), _binaryPath, File.Exists(modelPath), modelPath);
                return null;
            }
            if (!File.Exists(wavPath))
            {
                _logger.LogWarning("whisper transcription requested for missing clip {Path}", wavPath);
                return null;
            }

            try
            {
                // whisper.cpp saturates its threads for the whole transcription, so a
                // library walk runs it at idle priority (unless disabled in settings)
                // to yield CPU to anything else on the host.
                var settings = await _configurationService.GetApplicationSettingsAsync();
                var priorityClass = (settings?.VerificationLowCpuPriority ?? true)
                    ? ProcessPriorityClass.Idle
                    : (ProcessPriorityClass?)null;

                var stdout = await RunWhisperCliAsync(wavPath, modelPath, offsetMs: null, durationMs: null, priorityClass, cancellationToken);
                if (stdout == null) return null;

                var segments = ParseSegments(stdout);
                if (segments.Count == 0)
                {
                    // No timestamped lines (unexpected output shape) — fall back to
                    // the plain collapse.
                    return NormalizeTranscriptOutput(stdout);
                }

                // Gap re-probe: whisper decodes in 30s chunks, and a chunk whose
                // remainder is music-overlaid speech can be skipped wholesale —
                // live case: 0:02–0:30 (the entire spoken credits) came back as
                // dead air while the SAME span transcribed perfectly in isolation.
                // For every silent hole big enough to hide a credits block, re-run
                // the decoder on just that span (whisper-cli --offset-t/--duration)
                // and splice the recovered text in.
                var clipSeconds = TryGetWavDurationSeconds(wavPath) ?? segments[^1].End;
                var gaps = FindSilentGaps(segments, clipSeconds);
                foreach (var gap in gaps.Take(MaxGapProbes))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var probeStdout = await RunWhisperCliAsync(
                        wavPath,
                        modelPath,
                        offsetMs: (int)(gap.Start * 1000),
                        durationMs: (int)((gap.End - gap.Start) * 1000),
                        priorityClass,
                        cancellationToken);
                    var probeText = NormalizeTranscriptOutput(probeStdout);
                    if (!string.IsNullOrWhiteSpace(probeText))
                    {
                        _logger.LogInformation(
                            "whisper gap re-probe recovered {Chars} chars from {Start:0.0}s–{End:0.0}s of {Path}",
                            probeText.Length, gap.Start, gap.End, wavPath);
                        segments.Add(new TranscriptSegment(gap.Start, gap.End, probeText));
                    }
                }

                // Head re-probe: the chunk that OPENS the clip can swallow a short
                // spoken announcement wholesale — live case ("Before Eden"): the
                // first segment claimed 0:00–0:28 but carried only story text,
                // while decoding just the first 14s in isolation yielded
                // "Before Eden, by Arthur C. Clarke" verbatim. The gap re-probe
                // never sees this (there is no gap — the segment covers the span).
                //
                // CRITICAL: the probe must decode a PHYSICALLY TRUNCATED clip.
                // whisper-cli's --duration does NOT create a short decode window —
                // it still feeds the same fixed 30s mel chunk (only --offset
                // re-anchors it, which is why the mid-file gap probe works), so a
                // --duration head decode reproduces the identical swallowed
                // segment. Verified live on "Before Eden": --duration 14000
                // re-emitted the story text; a physically cut 15s file of the
                // same audio decoded "Before evening, by Arthur C. Clarke."
                //
                // Trigger is geometry OR text: a long head-claiming first segment
                // (the classic swallow shape), or opening text with no
                // announcement-shaped content at all — cheap insurance for
                // swallow shapes the geometry heuristic doesn't cover.
                string? headText = null;
                var probeGeometry = ShouldProbeHead(segments);
                var probeNoCredits = HeadLacksCreditText(segments);
                if (probeGeometry || probeNoCredits)
                {
                    var triggerReason = probeGeometry ? "geometry" : "no-credit-text";
                    cancellationToken.ThrowIfCancellationRequested();
                    var headClipPath = TryWriteHeadClip(wavPath, HeadProbeSeconds);
                    if (headClipPath == null)
                    {
                        // Once-per-job diagnostics at Information: the v2 probe
                        // failed here for DAYS invisibly (ffmpeg's LIST/INFO tag
                        // chunk defeated the canonical-header assumption) because
                        // this path was silent.
                        _logger.LogInformation(
                            "whisper head probe: triggered ({Reason}) but clip could not be written for {Path} — skipped",
                            triggerReason, wavPath);
                    }
                    else
                    {
                        try
                        {
                            var headStdout = await RunWhisperCliAsync(
                                headClipPath,
                                modelPath,
                                offsetMs: null,
                                durationMs: null,
                                priorityClass,
                                cancellationToken);
                            var probeText = NormalizeTranscriptOutput(headStdout);
                            // Compare against the WHOLE clip transcript: a long
                            // announcement spans several segments, and judging
                            // the probe against only the first one prepended
                            // near-verbatim doubles (live case "Holy Island").
                            var mainText = string.Join(" ", segments.OrderBy(s => s.Start).Select(s => s.Text));
                            if (HeadProbeRecoversNewText(mainText, probeText))
                            {
                                _logger.LogInformation(
                                    "whisper head probe: triggered ({Reason}), recovered {Chars} chars from 0–{End:0.0}s of {Path}",
                                    triggerReason, probeText!.Length, HeadProbeSeconds, wavPath);
                                headText = probeText;
                            }
                            else
                            {
                                _logger.LogInformation(
                                    "whisper head probe: triggered ({Reason}), no new text in 0–{End:0.0}s of {Path} (probe: {Probe})",
                                    triggerReason, HeadProbeSeconds, wavPath, Truncate(probeText, 120));
                            }
                        }
                        finally
                        {
                            try { File.Delete(headClipPath); }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                            {
                                _logger.LogDebug(ex, "could not delete head-probe clip {Path}", headClipPath);
                            }
                        }
                    }
                }
                else
                {
                    _logger.LogInformation(
                        "whisper head probe: skipped (opening text already announcement-shaped) for {Path}", wavPath);
                }

                var transcript = string.Join(" ", segments.OrderBy(s => s.Start).Select(s => s.Text)).Trim();
                if (headText != null)
                {
                    transcript = (headText + " " + transcript).Trim();
                }
                return transcript.Length == 0 ? null : transcript;
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

        private async Task<string?> RunWhisperCliAsync(
            string wavPath, string modelPath, int? offsetMs, int? durationMs, ProcessPriorityClass? priorityClass, CancellationToken cancellationToken)
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
            startInfo.ArgumentList.Add(modelPath);
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add(wavPath);
            // Deliberately NOT --no-timestamps: timestamp tokens anchor the
            // decoder, and without them base.en demonstrably skips
            // music-overlaid segments — on a live book the no-timestamps
            // decode dropped the entire spoken credits (0:09–0:30) while the
            // timestamped decode of the same clip transcribed them verbatim.
            // The segment timestamps are also what the gap re-probe needs.
            startInfo.ArgumentList.Add("--no-prints");
            startInfo.ArgumentList.Add("--language");
            startInfo.ArgumentList.Add("en");
            if (offsetMs is > 0)
            {
                startInfo.ArgumentList.Add("--offset-t");
                startInfo.ArgumentList.Add(offsetMs.Value.ToString());
            }
            if (durationMs is > 0)
            {
                startInfo.ArgumentList.Add("--duration");
                startInfo.ArgumentList.Add(durationMs.Value.ToString());
            }

            var result = await _processRunner.RunAsync(startInfo, TranscribeTimeoutMs, cancellationToken, priorityClass);
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
            return result.Stdout;
        }

        /// <summary>
        /// Duration of a canonical verification clip (16 kHz mono s16le WAV from
        /// the sample extractor), derived from the file size. Null when the file
        /// doesn't look like such a WAV.
        /// </summary>
        private static double? TryGetWavDurationSeconds(string wavPath)
        {
            try
            {
                const double bytesPerSecond = 16000 * 2; // 16 kHz, mono, 16-bit
                // Chunk-aware: extractor WAVs can carry a LIST/INFO tag chunk
                // before data, so file-size math over-counts.
                var located = TryLocateDataChunk(wavPath);
                if (located != null) return located.Value.DataSize / bytesPerSecond;

                var length = new FileInfo(wavPath).Length;
                const int headerBytes = 44;
                if (length <= headerBytes) return null;
                return (length - headerBytes) / bytesPerSecond;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return null;
            }
        }

        // Bound the extra decode work: one probe per gap, few gaps expected.
        private const int MaxGapProbes = 3;

        // Smaller holes are normal narration pauses / scene breaks; a credits
        // block needs roughly 10–30s, so 8s catches them without re-probing
        // every dramatic pause.
        private const double MinGapSeconds = 8.0;

        public sealed record TranscriptSegment(double Start, double End, string Text);

        // "[00:00:09.260 --> 00:00:17.160]   by Sarah J. Mass, ..." → segment text.
        private static readonly Regex SegmentTimestampRegex = new(
            @"^\s*\[\d{2}:\d{2}:\d{2}\.\d{3}\s*-->\s*\d{2}:\d{2}:\d{2}\.\d{3}\]\s*",
            RegexOptions.Compiled);

        private static readonly Regex SegmentLineRegex = new(
            @"^\s*\[(?<sh>\d{2}):(?<sm>\d{2}):(?<ss>\d{2})\.(?<sms>\d{3})\s*-->\s*(?<eh>\d{2}):(?<em>\d{2}):(?<es>\d{2})\.(?<ems>\d{3})\]\s*(?<text>.*\S)\s*$",
            RegexOptions.Compiled);

        /// <summary>Parses whisper-cli timestamped lines. Public + pure for unit testing.</summary>
        public static List<TranscriptSegment> ParseSegments(string? stdout)
        {
            var segments = new List<TranscriptSegment>();
            if (string.IsNullOrWhiteSpace(stdout)) return segments;

            foreach (var line in stdout.Split('\n'))
            {
                var match = SegmentLineRegex.Match(line);
                if (!match.Success) continue;
                segments.Add(new TranscriptSegment(
                    ToSeconds(match, "sh", "sm", "ss", "sms"),
                    ToSeconds(match, "eh", "em", "es", "ems"),
                    match.Groups["text"].Value.Trim()));
            }
            return segments;

            static double ToSeconds(Match m, string h, string min, string s, string ms) =>
                int.Parse(m.Groups[h].Value) * 3600
                + int.Parse(m.Groups[min].Value) * 60
                + int.Parse(m.Groups[s].Value)
                + int.Parse(m.Groups[ms].Value) / 1000.0;
        }

        /// <summary>
        /// Silent holes (leading, internal, trailing) of at least
        /// <see cref="MinGapSeconds"/> in a transcribed clip — the spans the
        /// decoder produced nothing for. Public + pure for unit testing.
        /// </summary>
        public static List<(double Start, double End)> FindSilentGaps(
            IReadOnlyList<TranscriptSegment> segments, double clipDurationSeconds)
        {
            var gaps = new List<(double Start, double End)>();
            if (segments.Count == 0) return gaps;

            var cursor = 0.0;
            foreach (var segment in segments.OrderBy(s => s.Start))
            {
                if (segment.Start - cursor >= MinGapSeconds)
                {
                    gaps.Add((cursor, segment.Start));
                }
                cursor = Math.Max(cursor, segment.End);
            }
            if (clipDurationSeconds - cursor >= MinGapSeconds)
            {
                gaps.Add((cursor, clipDurationSeconds));
            }
            return gaps;
        }

        /// <summary>
        /// Collapses whisper-cli's timestamped segment lines into the plain
        /// transcript the matcher consumes. Public + pure for unit testing.
        /// </summary>
        public static string? NormalizeTranscriptOutput(string? stdout)
        {
            if (string.IsNullOrWhiteSpace(stdout)) return null;

            var segments = stdout
                .Split('\n')
                .Select(line => SegmentTimestampRegex.Replace(line, string.Empty).Trim())
                .Where(line => line.Length > 0);

            var transcript = string.Join(" ", segments).Trim();
            return transcript.Length == 0 ? null : transcript;
        }

        private static string Truncate(string? text, int max) =>
            string.IsNullOrEmpty(text) ? string.Empty : (text.Length <= max ? text : text[..max]);
    }
}
