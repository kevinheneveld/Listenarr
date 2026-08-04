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
using Listenarr.Domain.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    /// <summary>
    /// Read-only resolution pass over records that hold duplicate copies of
    /// their own audio (evidence and grading via
    /// <see cref="DuplicateCopyProposalBuilder"/>). Records whose clusters are
    /// DIFFERENT books sharing one record are reported as Split Collection
    /// candidates instead of receiving keep/drop proposals. Proposes only —
    /// applying is the separate apply endpoint.
    /// </summary>
    public sealed class LibraryDuplicateCopyAnalysisWorkflow
    {
        private readonly IAudiobookRepository _repo;
        private readonly IAudiobookFileRepository _audioFileRepository;
        private readonly IFileSystem _fileSystem;
        private readonly DuplicateCopyProposalBuilder _builder;
        private readonly ILogger<LibraryDuplicateCopyAnalysisWorkflow> _logger;

        public LibraryDuplicateCopyAnalysisWorkflow(
            IAudiobookRepository repo,
            IAudiobookFileRepository audioFileRepository,
            IFileSystem fileSystem,
            DuplicateCopyProposalBuilder builder,
            ILogger<LibraryDuplicateCopyAnalysisWorkflow> logger)
        {
            _repo = repo;
            _audioFileRepository = audioFileRepository;
            _fileSystem = fileSystem;
            _builder = builder;
            _logger = logger;
        }

        public async Task<IActionResult> AnalyzeAsync(CancellationToken ct)
        {
            var books = await _repo.GetAllAsync();
            var files = await _audioFileRepository.GetAllAsync(ct);
            var filesByBook = files
                .GroupBy(f => f.AudiobookId)
                .ToDictionary(g => g.Key, g => g.ToList());

            var records = new List<object>();
            long totalReclaimable = 0;

            foreach (var book in books)
            {
                ct.ThrowIfCancellationRequested();
                if (!filesByBook.TryGetValue(book.Id, out var bookFiles) || bookFiles.Count < 2)
                {
                    continue;
                }

                var analysis = _builder.Resolve(book, bookFiles);
                if (analysis.Proposals.Count == 0 && !analysis.SplitCandidate) continue;

                var pathsById = bookFiles.ToDictionary(f => f.Id, f => f.Path ?? string.Empty);
                totalReclaimable += analysis.Proposals.Sum(p => p.Proposal.ReclaimableBytes);

                records.Add(new
                {
                    id = book.Id,
                    title = book.Title,
                    basePath = book.BasePath,
                    fileCount = bookFiles.Count,
                    splitCandidate = analysis.SplitCandidate,
                    proposals = analysis.Proposals
                        .Select(p => ShapeProposal(p, pathsById))
                        .ToList()
                });
            }

            _logger.LogInformation(
                "Duplicate-copy analysis: {Records} record(s) with findings, {Bytes} bytes reclaimable",
                records.Count, totalReclaimable);

            return new OkObjectResult(new
            {
                records,
                totalReclaimableBytes = totalReclaimable
            });
        }

        private object ShapeProposal(
            DuplicateCopyProposalBuilder.ResolvedProposal resolved,
            IReadOnlyDictionary<int, string> pathsById)
        {
            object ShapeCluster(DuplicateCopyAnalyzer.ClusterEvidence c) => new
            {
                key = c.Key,
                displayName = c.DisplayName,
                fileIds = c.FileIds,
                fileCount = c.FileCount,
                totalBytes = c.TotalBytes,
                totalDurationSeconds = c.TotalDurationSeconds,
                onDiskCount = c.FileIds.Count(id =>
                    pathsById.TryGetValue(id, out var p)
                    && !string.IsNullOrEmpty(p)
                    && _fileSystem.FileExists(p))
            };

            return new
            {
                keeper = ShapeCluster(resolved.Proposal.Keeper),
                redundant = resolved.Proposal.Redundant.Select(ShapeCluster).ToList(),
                confidence = resolved.Confidence,
                evidence = resolved.Evidence,
                reclaimableBytes = resolved.Proposal.ReclaimableBytes
            };
        }
    }
}
