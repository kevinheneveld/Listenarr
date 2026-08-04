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

using System.Security.Cryptography;
using Listenarr.Application.Audiobooks;
using Listenarr.Domain.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    /// <summary>
    /// Read-only resolution pass over records that hold duplicate copies of
    /// their own audio. For each record it clusters the files, asks
    /// <see cref="DuplicateCopyAnalyzer"/> which clusters are copies of each
    /// other and which to keep, then upgrades size-identical proposals to
    /// hash-confirmed by sampling the head and tail of each file pair.
    /// Proposes only — applying is a later, separate stage.
    /// </summary>
    public sealed class LibraryDuplicateCopyAnalysisWorkflow
    {
        private readonly IAudiobookRepository _repo;
        private readonly IAudiobookFileRepository _audioFileRepository;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger<LibraryDuplicateCopyAnalysisWorkflow> _logger;

        private const int SampleBytes = 4 << 20;

        public LibraryDuplicateCopyAnalysisWorkflow(
            IAudiobookRepository repo,
            IAudiobookFileRepository audioFileRepository,
            IFileSystem fileSystem,
            ILogger<LibraryDuplicateCopyAnalysisWorkflow> logger)
        {
            _repo = repo;
            _audioFileRepository = audioFileRepository;
            _fileSystem = fileSystem;
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

                var clusters = FileClustering.Cluster(bookFiles, book.BasePath);
                if (clusters.Count < 2) continue;

                var evidence = clusters
                    .Select(c => new DuplicateCopyAnalyzer.ClusterEvidence(
                        c.Key,
                        c.DisplayName,
                        c.Files.Select(f => f.Id).ToList(),
                        c.Files.Count,
                        c.Files.Sum(f => f.Size ?? 0),
                        c.Files.Sum(f => f.DurationSeconds ?? 0),
                        c.Files.All(f => f.DurationSeconds is > 0),
                        c.Files.Select(f => f.Size ?? 0).OrderBy(s => s).ToList()))
                    .ToList();

                var proposals = DuplicateCopyAnalyzer.Analyze(evidence);
                if (proposals.Count == 0) continue;

                var pathsById = bookFiles.ToDictionary(f => f.Id, f => f.Path ?? string.Empty);
                var shaped = proposals
                    .Select(p => ShapeProposal(p, pathsById))
                    .ToList();
                totalReclaimable += proposals.Sum(p => p.ReclaimableBytes);

                records.Add(new
                {
                    id = book.Id,
                    title = book.Title,
                    basePath = book.BasePath,
                    fileCount = bookFiles.Count,
                    proposals = shaped
                });
            }

            _logger.LogInformation(
                "Duplicate-copy analysis: {Records} record(s) with proposals, {Bytes} bytes reclaimable",
                records.Count, totalReclaimable);

            return new OkObjectResult(new
            {
                records,
                totalReclaimableBytes = totalReclaimable
            });
        }

        private object ShapeProposal(
            DuplicateCopyAnalyzer.CopyProposal proposal,
            IReadOnlyDictionary<int, string> pathsById)
        {
            var confidence = proposal.Confidence;
            var evidence = proposal.Evidence.ToList();

            // Size-identical proposals get a cheap disk check: sampled hash of
            // every keeper/redundant file pair (matched by size order). All
            // equal upgrades the proposal to hash-confirmed identical; any
            // mismatch demotes it to review — equal sizes with different bytes
            // is exactly the case a human must look at.
            if (proposal.SizesIdentical)
            {
                var verdict = ConfirmByHash(proposal, pathsById);
                if (verdict == true)
                {
                    confidence = DuplicateCopyAnalyzer.ConfidenceIdentical;
                    evidence.Add("Sampled content hash confirms the copies are identical");
                }
                else if (verdict == false)
                {
                    confidence = DuplicateCopyAnalyzer.ConfidenceReview;
                    evidence.Add("File sizes match but sampled content differs — inspect before acting");
                }
                else
                {
                    confidence = DuplicateCopyAnalyzer.ConfidenceReview;
                    evidence.Add("Some files are missing on disk; hash confirmation skipped");
                }
            }

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
                keeper = ShapeCluster(proposal.Keeper),
                redundant = proposal.Redundant.Select(ShapeCluster).ToList(),
                confidence,
                evidence,
                reclaimableBytes = proposal.ReclaimableBytes
            };
        }

        /// <summary>true = all pairs hash-equal; false = a mismatch; null = unreadable.</summary>
        private bool? ConfirmByHash(
            DuplicateCopyAnalyzer.CopyProposal proposal,
            IReadOnlyDictionary<int, string> pathsById)
        {
            try
            {
                var keeperHashes = HashClusterBySize(proposal.Keeper, pathsById);
                if (keeperHashes == null) return null;

                foreach (var redundant in proposal.Redundant)
                {
                    var redundantHashes = HashClusterBySize(redundant, pathsById);
                    if (redundantHashes == null) return null;
                    if (!keeperHashes.SequenceEqual(redundantHashes)) return false;
                }
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Sampled hash confirmation failed");
                return null;
            }
        }

        private List<string>? HashClusterBySize(
            DuplicateCopyAnalyzer.ClusterEvidence cluster,
            IReadOnlyDictionary<int, string> pathsById)
        {
            var result = new List<string>();
            foreach (var id in cluster.FileIds
                .OrderBy(id => pathsById.TryGetValue(id, out var p) && _fileSystem.FileExists(p)
                    ? _fileSystem.GetFileLength(p)
                    : -1))
            {
                if (!pathsById.TryGetValue(id, out var path)
                    || string.IsNullOrEmpty(path)
                    || !_fileSystem.FileExists(path))
                {
                    return null;
                }
                result.Add(SampledHash(path));
            }
            return result;
        }

        private string SampledHash(string path)
        {
            var length = _fileSystem.GetFileLength(path);
            using var md5 = MD5.Create();
            using var stream = _fileSystem.OpenReadStream(path);

            var buffer = new byte[SampleBytes];
            var read = ReadFully(stream, buffer);
            md5.TransformBlock(buffer, 0, read, null, 0);

            if (length > 2L * SampleBytes)
            {
                stream.Seek(-SampleBytes, SeekOrigin.End);
                read = ReadFully(stream, buffer);
                md5.TransformBlock(buffer, 0, read, null, 0);
            }

            md5.TransformFinalBlock([], 0, 0);
            return $"{length}:{Convert.ToHexString(md5.Hash!)}";
        }

        private static int ReadFully(Stream stream, byte[] buffer)
        {
            var total = 0;
            int read;
            while (total < buffer.Length
                && (read = stream.Read(buffer, total, buffer.Length - total)) > 0)
            {
                total += read;
            }
            return total;
        }
    }
}
