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

namespace Listenarr.Application.Search.Contracts
{
    /// <summary>
    /// Server-side queue behind the Wanted page's "Search All / Search Filtered"
    /// action. The UI hands over a list of audiobook ids once and the worker walks
    /// them one at a time, so the batch survives tab switches, background-tab timer
    /// throttling, and closing the browser — none of which a client-driven loop does.
    /// In-memory only: a process restart drops whatever was still pending.
    /// </summary>
    public interface IWantedSearchQueue
    {
        /// <summary>Queue ids for search. Ids already pending or running are skipped.</summary>
        WantedSearchEnqueueResult Enqueue(IReadOnlyCollection<int> audiobookIds);

        /// <summary>Drop everything pending and cancel the book currently being searched.</summary>
        void Cancel();

        /// <summary>Point-in-time view of the batch for the UI.</summary>
        WantedSearchQueueSnapshot Snapshot();

        // --- Worker-facing members ---------------------------------------------------

        /// <summary>Pending audiobook ids, in enqueue order.</summary>
        ChannelReader<int> Reader { get; }

        /// <summary>Cancelled by <see cref="Cancel"/> for the current batch.</summary>
        CancellationToken BatchToken { get; }

        void MarkStarted(int audiobookId, string? title);

        void MarkCompleted(int audiobookId, bool success, int downloadsQueued);

        /// <summary>Close the batch once nothing is pending and nothing is running.</summary>
        void MarkBatchFinishedIfDrained();
    }

    public sealed record WantedSearchEnqueueResult(int Accepted, int AlreadyQueued, int Pending, int Total);

    public sealed record WantedSearchQueueSnapshot(
        bool IsRunning,
        int Pending,
        int Processed,
        int Total,
        int Grabbed,
        int Failed,
        int? CurrentAudiobookId,
        string? CurrentTitle,
        DateTime? StartedAt,
        DateTime? CompletedAt,
        bool Cancelled)
    {
        public static WantedSearchQueueSnapshot Idle { get; } =
            new(false, 0, 0, 0, 0, 0, null, null, null, null, false);
    }
}
