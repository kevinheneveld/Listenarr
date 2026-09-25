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
using System.Threading.Channels;

namespace Listenarr.Application.Audiobooks.Organizing
{
    /// <summary>
    /// One confirmed "Organize library" row: everything the move enqueue needs,
    /// computed and validated inside the HTTP request, executed later by the
    /// batch worker.
    /// </summary>
    public sealed record OrganizeApplyItem(
        int AudiobookId,
        string? Title,
        string SourcePath,
        string TargetPath,
        bool ReplaceStubTarget,
        bool Repoint = false);

    /// <summary>A row the worker could not hand to the move queue.</summary>
    public sealed record OrganizeApplyProblem(int AudiobookId, string? Title, string Reason);

    /// <summary>A move job the worker queued on the batch's behalf.</summary>
    public sealed record OrganizeApplyQueuedJob(string JobId, int AudiobookId, string? AudiobookTitle, string? TargetPath);

    /// <summary>A row the batch resolved by rewriting the record's stored path (no move job).</summary>
    public sealed record OrganizeApplyRepointedRow(int AudiobookId, string? AudiobookTitle, string? TargetPath);

    /// <summary>
    /// Outcome of one enqueue attempt (see <see cref="IOrganizeMoveEnqueuer"/>).
    /// <paramref name="Repointed"/> is true when the item was a repoint that
    /// completed in place — accepted, but there is no job to track.
    /// </summary>
    public sealed record OrganizeMoveEnqueueOutcome(bool Accepted, Guid? JobId, string? Reason, bool Repointed = false);

    /// <summary>
    /// Hands one organize row to the durable move queue with the same checks
    /// the per-book Organize button runs. Implemented in the API layer over
    /// the move workflow; the batch worker resolves it per item.
    /// </summary>
    public interface IOrganizeMoveEnqueuer
    {
        Task<OrganizeMoveEnqueueOutcome> EnqueueAsync(OrganizeApplyItem item, CancellationToken ct = default);
    }

    public sealed record OrganizeApplyBatchSnapshot(
        Guid? BatchId,
        bool IsRunning,
        int Total,
        int Processed,
        int Queued,
        int NotAccepted,
        int Failed,
        int Repointed,
        int? CurrentAudiobookId,
        string? CurrentTitle,
        DateTime? StartedAt,
        DateTime? CompletedAt,
        bool Cancelled,
        IReadOnlyList<OrganizeApplyQueuedJob> QueuedJobs,
        IReadOnlyList<OrganizeApplyRepointedRow> RepointedRows,
        IReadOnlyList<OrganizeApplyProblem> Problems)
    {
        public static OrganizeApplyBatchSnapshot Idle { get; } =
            new(null, false, 0, 0, 0, 0, 0, 0, null, null, null, null, false, Array.Empty<OrganizeApplyQueuedJob>(), Array.Empty<OrganizeApplyRepointedRow>(), Array.Empty<OrganizeApplyProblem>());
    }

    /// <summary>
    /// Server-side "Organize library" batch. The apply request validates and
    /// computes targets, then hands the rows here and returns; a hosted worker
    /// drains them one at a time into the move queue. Live case: queuing 726
    /// moves inline took 3–11 s per row once the move worker held the
    /// filesystem-mutation lock, the reverse proxy cut the request at 60 s, and
    /// 709 rows were never queued. One batch at a time.
    /// </summary>
    public interface IOrganizeApplyBatch
    {
        /// <summary>Starts a batch; false (and no batch id) when one is still running.</summary>
        bool TryStart(IReadOnlyList<OrganizeApplyItem> items, out Guid batchId);

        /// <summary>Stop feeding the move queue; moves already queued proceed.</summary>
        void Cancel();

        OrganizeApplyBatchSnapshot Snapshot();

        // --- Worker-facing members ---------------------------------------------------

        ChannelReader<OrganizeApplyItem> Reader { get; }
        CancellationToken BatchToken { get; }
        void MarkStarted(OrganizeApplyItem item);
        void MarkQueued(OrganizeApplyItem item, Guid jobId);
        void MarkRepointed(OrganizeApplyItem item);
        void MarkNotAccepted(OrganizeApplyItem item, string reason);
        void MarkFailed(OrganizeApplyItem item, string reason);
        void MarkBatchFinishedIfDrained();
    }
}
