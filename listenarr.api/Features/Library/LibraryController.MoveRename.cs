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

using Listenarr.Api.Attributes;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    public partial class LibraryController
    {
        /// <summary>
        /// Get in-memory scan job status by jobId (debugging/admin helper).
        /// </summary>
        [HttpGet("scan/{jobId}")]
        public IActionResult GetScanJobStatus(string jobId)
        {
            return _scanQueueWorkflow.GetStatus(jobId);
        }

        /// <summary>
        /// Enqueue a background job to move an audiobook's files to a new destination path.
        /// </summary>
        /// <param name="id">Audiobook ID.</param>
        /// <param name="request">Move request with destination path and optional source override.</param>
        /// <param name="cancellationToken">Request cancellation token.</param>
        /// <returns>Accepted with a job ID that can be polled for progress.</returns>
        [HttpPost("{id}/move")]
        public async Task<IActionResult> EnqueueMove(
            int id,
            [FromBody] MoveRequest request,
            CancellationToken cancellationToken = default)
        {
            return await _moveWorkflow.EnqueueAsync(id, request, cancellationToken);
        }

        /// <summary>
        /// Get the durable unresolved move state for an audiobook.
        /// </summary>
        /// <param name="id">Audiobook ID.</param>
        /// <param name="cancellationToken">Request cancellation token.</param>
        [HttpGet("{id}/move/recovery")]
        public async Task<IActionResult> GetMoveRecoveryState(
            int id,
            CancellationToken cancellationToken)
        {
            return await _moveWorkflow.GetRecoveryStateAsync(id, cancellationToken);
        }

        /// <summary>
        /// Get active file-move background jobs for activity recovery.
        /// </summary>
        /// <param name="cancellationToken">Request cancellation token.</param>
        [HttpGet("move")]
        public async Task<IActionResult> GetActiveMoveJobs(CancellationToken cancellationToken)
        {
            return await _moveWorkflow.GetActiveAsync(cancellationToken);
        }

        /// <summary>
        /// Get the current status of a file-move background job.
        /// </summary>
        /// <param name="jobId">The GUID returned when the move was enqueued.</param>
        /// <param name="cancellationToken">Request cancellation token.</param>
        [HttpGet("move/{jobId}")]
        public async Task<IActionResult> GetMoveJobStatus(string jobId, CancellationToken cancellationToken)
        {
            return await _moveWorkflow.GetStatusAsync(jobId, cancellationToken);
        }

        /// <summary>
        /// Re-enqueue a failed, needs-attention, or already queued move job for safe repair.
        /// </summary>
        /// <param name="jobId">Original move job GUID.</param>
        /// <param name="cancellationToken">Request cancellation token.</param>
        /// <returns>Accepted with the new job ID.</returns>
        [HttpPost("move/requeue/{jobId}")]
        public async Task<IActionResult> RequeueMoveJob(
            string jobId,
            CancellationToken cancellationToken = default)
        {
            return await _moveWorkflow.RequeueAsync(jobId, cancellationToken);
        }

        /// <summary>
        /// Re-enqueue a previously failed or completed scan job for retry.
        /// </summary>
        /// <param name="jobId">Original scan job GUID.</param>
        /// <returns>Accepted with the new job ID.</returns>
        [HttpPost("scan/requeue/{jobId}")]
        public async Task<IActionResult> RequeueScanJob(string jobId)
        {
            return await _scanQueueWorkflow.RequeueAsync(jobId);
        }

        [HttpPost("rename/preview")]
        public async Task<IActionResult> PreviewRename([FromBody] BulkRenameRequest request, CancellationToken ct)
        {
            return await _renameWorkflow.PreviewAsync(request, ct);
        }

        [HttpPost("rename")]
        public async Task<IActionResult> ExecuteRename([FromBody] ExecuteRenameRequest request, CancellationToken ct)
        {
            return await _renameWorkflow.ExecuteAsync(request, ct);
        }

        [HttpPost("{id}/rename/preview")]
        public async Task<IActionResult> PreviewRenameSingle(int id, CancellationToken ct)
        {
            return await _renameWorkflow.PreviewSingleAsync(id, ct);
        }

        [HttpPost("{id}/rename")]
        public async Task<IActionResult> ExecuteRenameSingle(int id, [FromBody] RenameOperation operation, CancellationToken ct)
        {
            return await _renameWorkflow.ExecuteSingleAsync(id, operation, ct);
        }
    }
}
