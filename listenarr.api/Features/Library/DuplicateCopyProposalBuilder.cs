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

namespace Listenarr.Api.Features.Library
{
    /// <summary>
    /// Shared core for the duplicate-copy analyze and apply workflows: cluster
    /// a record's files, run <see cref="DuplicateCopyAnalyzer"/>, then upgrade
    /// size-identical proposals to hash-confirmed by sampling head and tail of
    /// each file pair (or demote them to review on mismatch). The apply stage
    /// re-runs THIS to re-verify a proposal before deleting anything, so a
    /// stale request can never delete files the current analysis wouldn't.
    /// </summary>
    public sealed class DuplicateCopyProposalBuilder
    {
        private readonly IFileSystem _fileSystem;
        private readonly ILogger<DuplicateCopyProposalBuilder> _logger;

        private const int SampleBytes = 4 << 20;

        public DuplicateCopyProposalBuilder(
            IFileSystem fileSystem,
            ILogger<DuplicateCopyProposalBuilder> logger)
        {
            _fileSystem = fileSystem;
            _logger = logger;
        }

        public sealed record ResolvedProposal(
            DuplicateCopyAnalyzer.CopyProposal Proposal,
            string Confidence,
            IReadOnlyList<string> Evidence);

        public sealed record ResolvedAnalysis(
            IReadOnlyList<ResolvedProposal> Proposals,
            bool SplitCandidate);

        public ResolvedAnalysis Resolve(Audiobook book, IReadOnlyList<AudiobookFile> files)
        {
            var clusters = FileClustering.Cluster(files, book.BasePath);
            if (clusters.Count < 2) return new ResolvedAnalysis([], false);

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

            var analysis = DuplicateCopyAnalyzer.Analyze(evidence);
            if (analysis.Proposals.Count == 0)
            {
                return new ResolvedAnalysis([], analysis.SplitCandidate);
            }

            var pathsById = files.ToDictionary(f => f.Id, f => f.Path ?? string.Empty);
            var resolved = analysis.Proposals
                .Select(p => ResolveConfidence(p, pathsById))
                .ToList();
            return new ResolvedAnalysis(resolved, analysis.SplitCandidate);
        }

        private ResolvedProposal ResolveConfidence(
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

            return new ResolvedProposal(proposal, confidence, evidence);
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
