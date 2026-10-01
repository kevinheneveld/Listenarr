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
    /// Split Collection's "is this copy better than what the destination
    /// already has?" check: weighs a group of the source record's files
    /// against the files already on the chosen destination record (see
    /// <see cref="CopyQualityComparer"/>). Read-only — replacing is a
    /// transfer followed by deletes of the listed existing files, driven by
    /// the client like every other split action.
    /// </summary>
    public sealed class LibrarySplitCompareWorkflow
    {
        private readonly IAudiobookRepository _repo;
        private readonly IAudiobookFileRepository _audioFileRepository;

        public LibrarySplitCompareWorkflow(
            IAudiobookRepository repo,
            IAudiobookFileRepository audioFileRepository)
        {
            _repo = repo;
            _audioFileRepository = audioFileRepository;
        }

        public sealed class CompareRequest
        {
            public int TargetAudiobookId { get; set; }

            /// <summary>The group's files — must belong to the source record.</summary>
            public List<int>? FileIds { get; set; }
        }

        public async Task<IActionResult> CompareAsync(int id, CompareRequest? request, CancellationToken ct)
        {
            if (request == null || request.TargetAudiobookId <= 0 || request.FileIds is not { Count: > 0 })
            {
                return new BadRequestObjectResult(new { message = "targetAudiobookId and fileIds are required" });
            }
            if (request.TargetAudiobookId == id)
            {
                return new BadRequestObjectResult(new { message = "The destination must be a different record" });
            }

            var audiobook = await _repo.GetByIdAsync(id);
            var target = await _repo.GetByIdAsync(request.TargetAudiobookId);
            if (audiobook == null || target == null)
            {
                return new NotFoundObjectResult(new { message = "Audiobook not found" });
            }

            var wanted = request.FileIds.ToHashSet();
            var incoming = (await _audioFileRepository.GetByAudiobookIdAsync(id, ct))
                .Where(f => wanted.Contains(f.Id))
                .ToList();
            if (incoming.Count == 0)
            {
                return new BadRequestObjectResult(new { message = $"None of the files belong to audiobook {id}" });
            }

            var existing = await _audioFileRepository.GetByAudiobookIdAsync(target.Id, ct);
            double? expectedRuntimeSeconds = target.Runtime is > 0 ? target.Runtime.Value * 60.0 : null;
            var comparison = CopyQualityComparer.Compare(
                CopyQualityComparer.Summarize(incoming),
                CopyQualityComparer.Summarize(existing),
                expectedRuntimeSeconds,
                CopyQualityComparer.SizesIdentical(incoming, existing));

            return new OkObjectResult(new
            {
                audiobookId = id,
                targetId = target.Id,
                targetTitle = target.Title,
                expectedRuntimeSeconds,
                incoming = SplitGroupStats.Describe(incoming),
                existing = SplitGroupStats.Describe(existing),
                existingFileIds = existing.Select(f => f.Id).ToList(),
                verdict = comparison.Verdict,
                reason = comparison.Reason
            });
        }
    }
}
