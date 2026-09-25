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
using Listenarr.Application.Audiobooks.Organizing;
using Listenarr.Application.Common.Exceptions;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    /// <summary>
    /// The organize batch worker's way into the durable move queue: one row →
    /// the same <see cref="LibraryMoveWorkflow.EnqueueAsync"/> the per-book
    /// Organize button uses, with its result folded into an accepted / not
    /// accepted outcome.
    /// </summary>
    public sealed class OrganizeMoveEnqueuer : IOrganizeMoveEnqueuer
    {
        private readonly LibraryMoveWorkflow _moveWorkflow;
        private readonly IAudiobookDestinationRewriteService _destinationRewriteService;

        public OrganizeMoveEnqueuer(
            LibraryMoveWorkflow moveWorkflow,
            IAudiobookDestinationRewriteService destinationRewriteService)
        {
            _moveWorkflow = moveWorkflow;
            _destinationRewriteService = destinationRewriteService;
        }

        public async Task<OrganizeMoveEnqueueOutcome> EnqueueAsync(OrganizeApplyItem item, CancellationToken ct = default)
        {
            if (item.Repoint)
            {
                // The files are already where the pattern wants them; only the
                // record's stored path lags. Same code path as the per-book
                // "move" with moveFiles:false — a BasePath rewrite under the
                // filesystem-mutation lock, no move job, no bytes touched.
                try
                {
                    await _destinationRewriteService.RewriteDestinationAsync(
                        item.AudiobookId,
                        item.TargetPath,
                        item.SourcePath,
                        ct);
                    return new OrganizeMoveEnqueueOutcome(true, null, null, Repointed: true);
                }
                catch (ListenarrApplicationException ex)
                {
                    return new OrganizeMoveEnqueueOutcome(false, null, ex.Message);
                }
            }

            var result = await _moveWorkflow.EnqueueAsync(
                item.AudiobookId,
                new LibraryController.MoveRequest
                {
                    DestinationPath = item.TargetPath,
                    SourcePath = item.SourcePath,
                    MoveFiles = true,
                    ReplaceStubTarget = item.ReplaceStubTarget,
                },
                ct);

            if (result is AcceptedResult { Value: MoveEnqueuedResponse enqueued })
            {
                return new OrganizeMoveEnqueueOutcome(true, enqueued.JobId, null);
            }

            var reason = result is ObjectResult { Value: not null } obj
                ? obj.Value!.ToString()
                : result.GetType().Name;
            return new OrganizeMoveEnqueueOutcome(false, null, reason);
        }
    }
}
