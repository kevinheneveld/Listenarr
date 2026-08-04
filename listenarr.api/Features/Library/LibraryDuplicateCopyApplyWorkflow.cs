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
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    /// <summary>
    /// Apply stage for duplicate-copy proposals: deletes a proposal's
    /// redundant files (disk + tracking). Every application is re-verified
    /// against a FRESH analysis first — the request's redundant file set must
    /// exactly match a current proposal at identical/high confidence, so a
    /// stale browser tab or a changed disk can never delete what the current
    /// evidence wouldn't. Review-tier proposals are refused outright.
    /// </summary>
    public sealed class LibraryDuplicateCopyApplyWorkflow
    {
        private readonly IAudiobookRepository _repo;
        private readonly IAudiobookFileRepository _audioFileRepository;
        private readonly IAudiobookFileService _audiobookFileService;
        private readonly DuplicateCopyProposalBuilder _builder;
        private readonly ILogger<LibraryDuplicateCopyApplyWorkflow> _logger;

        public LibraryDuplicateCopyApplyWorkflow(
            IAudiobookRepository repo,
            IAudiobookFileRepository audioFileRepository,
            IAudiobookFileService audiobookFileService,
            DuplicateCopyProposalBuilder builder,
            ILogger<LibraryDuplicateCopyApplyWorkflow> logger)
        {
            _repo = repo;
            _audioFileRepository = audioFileRepository;
            _audiobookFileService = audiobookFileService;
            _builder = builder;
            _logger = logger;
        }

        public async Task<IActionResult> ApplyAsync(
            LibraryController.ApplyDuplicateCopiesRequest? request,
            CancellationToken ct)
        {
            if (request?.Applications == null || request.Applications.Count == 0)
            {
                return new BadRequestObjectResult(new { message = "No applications provided" });
            }

            var results = new List<object>();
            var totalDeleted = 0;
            long totalFreed = 0;

            foreach (var app in request.Applications)
            {
                ct.ThrowIfCancellationRequested();
                var (result, deleted, freed) = await ApplyOneAsync(app, ct);
                results.Add(result);
                totalDeleted += deleted;
                totalFreed += freed;
            }

            return new OkObjectResult(new
            {
                results,
                totalFilesDeleted = totalDeleted,
                totalFreedBytes = totalFreed
            });
        }

        private async Task<(object Result, int Deleted, long Freed)> ApplyOneAsync(
            LibraryController.ApplyDuplicateCopyItem app,
            CancellationToken ct)
        {
            object Failure(string reason) => new
            {
                audiobookId = app.AudiobookId,
                applied = false,
                reason
            };

            var book = await _repo.GetByIdAsync(app.AudiobookId);
            if (book == null) return (Failure("Audiobook not found"), 0, 0);

            var requested = new HashSet<int>(app.RedundantFileIds ?? []);
            if (requested.Count == 0) return (Failure("No redundant file ids provided"), 0, 0);

            // Re-verify against a fresh analysis: the requested delete set must
            // exactly match a current proposal's redundant set.
            var files = await _audioFileRepository.GetByAudiobookIdAsync(app.AudiobookId, ct);
            var analysis = _builder.Resolve(book, files);
            var match = analysis.Proposals.FirstOrDefault(p =>
                requested.SetEquals(p.Proposal.Redundant.SelectMany(c => c.FileIds)));
            if (match == null)
            {
                return (Failure("Proposal no longer matches the current analysis — re-analyze first"), 0, 0);
            }
            if (match.Confidence != DuplicateCopyAnalyzer.ConfidenceIdentical
                && match.Confidence != DuplicateCopyAnalyzer.ConfidenceHigh)
            {
                return (Failure($"Confidence '{match.Confidence}' is below the apply bar"), 0, 0);
            }

            var sizeById = files.ToDictionary(f => f.Id, f => f.Size ?? 0);
            var deleted = 0;
            long freed = 0;
            var warnings = new List<string>();

            foreach (var fileId in requested)
            {
                try
                {
                    var result = await _audiobookFileService.DeleteAudiobookFileAsync(
                        book, fileId, deleteFromDisk: true, source: "duplicate-copy-apply", ct: ct);
                    if (result.Outcome == DeleteAudiobookFileOutcome.Deleted)
                    {
                        deleted++;
                        freed += sizeById.GetValueOrDefault(fileId);
                        warnings.AddRange(result.Warnings);
                    }
                    else
                    {
                        warnings.Add($"File {fileId}: {result.Outcome}");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Duplicate-copy apply failed deleting file {FileId} of audiobook {AudiobookId}",
                        fileId, app.AudiobookId);
                    warnings.Add($"File {fileId}: delete failed");
                }
            }

            _logger.LogInformation(
                "Duplicate-copy apply for audiobook {AudiobookId} ({Title}): {Deleted}/{Requested} file(s) deleted, {Freed} bytes freed ({Confidence})",
                book.Id, LogRedaction.SanitizeText(book.Title), deleted, requested.Count, freed, match.Confidence);

            return (new
            {
                audiobookId = app.AudiobookId,
                applied = true,
                confidence = match.Confidence,
                filesDeleted = deleted,
                freedBytes = freed,
                warnings
            }, deleted, freed);
        }
    }
}
