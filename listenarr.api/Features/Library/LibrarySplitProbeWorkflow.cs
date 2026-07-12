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

using Listenarr.Application.Audiobooks;
using Listenarr.Application.Audiobooks.Verification.Contracts;
using Listenarr.Domain.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    /// <summary>
    /// Audio-probe mode for Split Collection (see <see cref="SplitAudioProbePlanner"/>):
    /// when name/tag clustering can't split an anonymized collection, the
    /// client fetches probe candidates (shape-derived boundary suspects),
    /// transcribes them ONE PER REQUEST — each probe is a ~40s whisper run,
    /// and a batch would outlive reverse-proxy timeouts, the same constraint
    /// that shaped the AI sweep — then submits the transcripts for a group
    /// plan rendered through the normal split-preview UI.
    /// </summary>
    public sealed class LibrarySplitProbeWorkflow
    {
        private readonly IAudiobookRepository _repo;
        private readonly IAudiobookFileRepository _audioFileRepository;
        private readonly IWhisperService _whisperService;
        private readonly IAudioSampleExtractor _sampleExtractor;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger<LibrarySplitProbeWorkflow> _logger;

        public LibrarySplitProbeWorkflow(
            IAudiobookRepository repo,
            IAudiobookFileRepository audioFileRepository,
            IWhisperService whisperService,
            IAudioSampleExtractor sampleExtractor,
            IFileSystem fileSystem,
            ILogger<LibrarySplitProbeWorkflow> logger)
        {
            _repo = repo;
            _audioFileRepository = audioFileRepository;
            _whisperService = whisperService;
            _sampleExtractor = sampleExtractor;
            _fileSystem = fileSystem;
            _logger = logger;
        }

        public sealed class ProbeRequest
        {
            public int FileId { get; set; }

            /// <summary>Seconds of opening audio to transcribe (clamped 30–180).</summary>
            public int? Seconds { get; set; }
        }

        public sealed class ProbePlanRequest
        {
            public List<ProbePlanEntry>? Probes { get; set; }
        }

        public sealed class ProbePlanEntry
        {
            public int FileId { get; set; }
            public string? Transcript { get; set; }
        }

        /// <summary>Shape-derived probe candidates for the record's files.</summary>
        public async Task<IActionResult> CandidatesAsync(int id, CancellationToken ct)
        {
            var audiobook = await _repo.GetByIdAsync(id);
            if (audiobook == null)
            {
                return new NotFoundObjectResult(new { message = "Audiobook not found" });
            }

            var shapes = await OrderedShapesAsync(id, ct);
            var candidates = SplitAudioProbePlanner.PickProbeCandidates(shapes);
            var whisperAvailable = await _whisperService.IsAvailableAsync();

            return new OkObjectResult(new
            {
                audiobookId = id,
                whisperAvailable,
                totalFiles = shapes.Count,
                candidates = candidates.Select(c => new
                {
                    fileId = c.FileId,
                    fileName = c.FileName,
                    reason = c.Reason
                }).ToList()
            });
        }

        /// <summary>Transcribe the opening of ONE file (proxy-safe unit of work).</summary>
        public async Task<IActionResult> ProbeAsync(int id, ProbeRequest? request, CancellationToken ct)
        {
            if (request == null || request.FileId <= 0)
            {
                return new BadRequestObjectResult(new { message = "fileId is required" });
            }

            var audiobook = await _repo.GetByIdAsync(id);
            if (audiobook == null)
            {
                return new NotFoundObjectResult(new { message = "Audiobook not found" });
            }

            var file = (await _audioFileRepository.GetByAudiobookIdAsync(id, ct))
                .FirstOrDefault(f => f.Id == request.FileId);
            if (file == null)
            {
                return new BadRequestObjectResult(new { message = $"File {request.FileId} does not belong to audiobook {id}" });
            }

            if (!await _whisperService.IsAvailableAsync())
            {
                return new BadRequestObjectResult(new { message = "Whisper is not available — audio probing needs the bundled whisper.cpp binary and model." });
            }

            var path = FileUtils.CombineWithOptionalBase(audiobook.BasePath, file.Path ?? string.Empty);
            if (string.IsNullOrWhiteSpace(path) || !_fileSystem.FileExists(path))
            {
                return new BadRequestObjectResult(new { message = $"File {request.FileId} is missing on disk" });
            }

            var seconds = Math.Clamp(request.Seconds ?? SplitAudioProbePlanner.DefaultProbeSeconds, 30, 180);
            await using var samples = await _sampleExtractor.ExtractAsync(
                path, null, new AudioSampleStrategy(OpeningSeconds: seconds, ClosingSeconds: 0), ct);
            if (samples.OpeningClipPath == null)
            {
                return new ObjectResult(new { message = "Could not extract an audio clip from this file" }) { StatusCode = 500 };
            }

            var transcript = await _whisperService.TranscribeAsync(samples.OpeningClipPath, ct);
            _logger.LogInformation(
                "Split audio probe on audiobook {AudiobookId} file {FileId}: {Chars} transcript char(s)",
                id, request.FileId, transcript?.Length ?? 0);

            return new OkObjectResult(new
            {
                fileId = request.FileId,
                transcript
            });
        }

        /// <summary>
        /// Turn collected probe transcripts into proposed groups, with a
        /// suggested existing-record destination per labeled group (the same
        /// suggester the name-clustering preview uses).
        /// </summary>
        public async Task<IActionResult> PlanAsync(int id, ProbePlanRequest? request, CancellationToken ct)
        {
            var audiobook = await _repo.GetByIdAsync(id);
            if (audiobook == null)
            {
                return new NotFoundObjectResult(new { message = "Audiobook not found" });
            }

            var probes = (request?.Probes ?? new List<ProbePlanEntry>())
                .Select(p => new SplitAudioProbePlanner.ProbeResult(p.FileId, p.Transcript))
                .ToList();
            if (probes.Count == 0)
            {
                return new BadRequestObjectResult(new { message = "At least one probe transcript is required" });
            }

            var shapes = await OrderedShapesAsync(id, ct);
            var nameById = shapes.ToDictionary(s => s.Id, s => s.Name);
            var groups = SplitAudioProbePlanner.BuildGroups(shapes, probes);

            // Destination suggestions, same-author shelf first — identical
            // candidate ordering to the name-clustering preview.
            var all = await _repo.GetAllAsync();
            var sourceAuthors = new HashSet<string>(
                audiobook.Authors ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            var sameAuthor = new List<(int, string)>();
            var others = new List<(int, string)>();
            foreach (var candidate in all)
            {
                if (candidate.Id == id || string.IsNullOrWhiteSpace(candidate.Title)) continue;
                var isSameAuthor = (candidate.Authors ?? new List<string>()).Any(sourceAuthors.Contains);
                (isSameAuthor ? sameAuthor : others).Add((candidate.Id, candidate.Title!));
            }
            var titleById = all.Where(a => a.Id != id).ToDictionary(a => a.Id, a => a.Title ?? string.Empty);

            var response = groups.Select((g, index) =>
            {
                var suggested = g.Label == null
                    ? null
                    : SplitDestinationSuggester.Suggest(g.Label, sameAuthor)
                        ?? SplitDestinationSuggester.Suggest(g.Label, others);
                return new
                {
                    key = $"probe-{index}",
                    displayName = g.DisplayName,
                    label = g.Label,
                    fileIds = g.FileIds,
                    fileNames = g.FileIds
                        .Select(fid => nameById.GetValueOrDefault(fid, string.Empty))
                        .Where(n => n.Length > 0)
                        .ToList(),
                    boundaryTranscript = g.BoundaryTranscript,
                    suggestedTargetId = suggested,
                    suggestedTargetTitle = suggested.HasValue && titleById.TryGetValue(suggested.Value, out var t) ? t : null,
                    suggestionSource = suggested == null ? null : "audio-probe"
                };
            }).ToList();

            _logger.LogInformation(
                "Split audio-probe plan for audiobook {AudiobookId}: {Groups} group(s) from {Probes} probe(s) over {Files} file(s)",
                id, response.Count, probes.Count, shapes.Count);

            return new OkObjectResult(new { audiobookId = id, clusters = response });
        }

        private async Task<List<SplitAudioProbePlanner.FileShape>> OrderedShapesAsync(int id, CancellationToken ct)
        {
            var files = await _audioFileRepository.GetByAudiobookIdAsync(id, ct);
            return AudiobookFileOrdering.InNaturalOrder(files)
                .Select(f => new SplitAudioProbePlanner.FileShape(
                    f.Id,
                    Path.GetFileName(f.Path ?? string.Empty),
                    f.Size,
                    f.DurationSeconds))
                .ToList();
        }
    }
}
