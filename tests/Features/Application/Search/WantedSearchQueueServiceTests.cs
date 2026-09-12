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

using Listenarr.Application.Search.WantedSearch;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Search
{
    [Trait("Name", "WantedSearchQueueServiceTests")]
    [Trait("Category", "Search")]
    public class WantedSearchQueueServiceTests : BaseTests
    {
        private static WantedSearchQueueService CreateQueue() =>
            new(NullLogger<WantedSearchQueueService>.Instance);

        private static List<int> Drain(IWantedSearchQueue queue)
        {
            var ids = new List<int>();
            while (queue.Reader.TryRead(out var id))
            {
                ids.Add(id);
            }
            return ids;
        }

        [Fact]
        public void Enqueue_DedupesPendingIds_AndIgnoresNonPositive()
        {
            var queue = CreateQueue();

            var first = queue.Enqueue(new[] { 1, 2, 3 });
            var second = queue.Enqueue(new[] { 2, 3, 4, 0, -1 });

            Assert.Equal(3, first.Accepted);
            Assert.Equal(0, first.AlreadyQueued);
            Assert.Equal(1, second.Accepted);
            Assert.Equal(2, second.AlreadyQueued);
            Assert.Equal(4, second.Total);
            Assert.Equal(4, second.Pending);
            Assert.Equal(new[] { 1, 2, 3, 4 }, Drain(queue));
            Assert.True(queue.Snapshot().IsRunning);
        }

        [Fact]
        public void Enqueue_SkipsTheBookCurrentlyInFlight()
        {
            var queue = CreateQueue();
            queue.Enqueue(new[] { 7 });
            queue.Reader.TryRead(out _);
            queue.MarkStarted(7, "Seven");

            var result = queue.Enqueue(new[] { 7, 8 });

            Assert.Equal(1, result.Accepted);
            Assert.Equal(1, result.AlreadyQueued);
            Assert.Equal(7, queue.Snapshot().CurrentAudiobookId);
            Assert.Equal("Seven", queue.Snapshot().CurrentTitle);
        }

        [Fact]
        public void Counters_ProgressThroughStartedAndCompleted()
        {
            var queue = CreateQueue();
            queue.Enqueue(new[] { 1, 2, 3 });

            queue.MarkStarted(1, "One");
            queue.MarkCompleted(1, success: true, downloadsQueued: 1);
            queue.MarkStarted(2, "Two");
            queue.MarkCompleted(2, success: true, downloadsQueued: 0);
            queue.MarkStarted(3, "Three");
            queue.MarkCompleted(3, success: false, downloadsQueued: 0);

            var snapshot = queue.Snapshot();
            Assert.Equal(3, snapshot.Processed);
            Assert.Equal(3, snapshot.Total);
            Assert.Equal(1, snapshot.Grabbed);
            Assert.Equal(1, snapshot.Failed);
            Assert.Equal(0, snapshot.Pending);
            Assert.Null(snapshot.CurrentAudiobookId);
            Assert.True(snapshot.IsRunning, "batch stays open until the worker closes it");
        }

        [Fact]
        public void MarkBatchFinishedIfDrained_ClosesTheBatch_OnlyWhenNothingIsLeft()
        {
            var queue = CreateQueue();
            queue.Enqueue(new[] { 1, 2 });

            queue.MarkStarted(1, null);
            queue.MarkBatchFinishedIfDrained();
            Assert.True(queue.Snapshot().IsRunning, "one pending + one running");

            queue.MarkCompleted(1, true, 0);
            queue.MarkBatchFinishedIfDrained();
            Assert.True(queue.Snapshot().IsRunning, "one still pending");

            queue.MarkStarted(2, null);
            queue.MarkCompleted(2, true, 1);
            queue.MarkBatchFinishedIfDrained();

            var snapshot = queue.Snapshot();
            Assert.False(snapshot.IsRunning);
            Assert.NotNull(snapshot.CompletedAt);
            Assert.NotNull(snapshot.StartedAt);
            Assert.False(snapshot.Cancelled);
        }

        [Fact]
        public void Cancel_DrainsPending_CancelsBatchToken_AndMarksCancelled()
        {
            var queue = CreateQueue();
            queue.Enqueue(new[] { 1, 2, 3 });
            var token = queue.BatchToken;
            var oldReader = queue.Reader;
            queue.MarkStarted(1, null);

            queue.Cancel();

            Assert.True(token.IsCancellationRequested);
            // The old channel is completed for the worker (its Completion task only
            // settles once the leftover ids are drained, so probe the writer side).
            Assert.True(oldReader.TryRead(out _), "leftover ids stay on the retired channel");
            var snapshot = queue.Snapshot();
            Assert.True(snapshot.Cancelled);
            Assert.False(snapshot.IsRunning);
            Assert.Equal(0, snapshot.Pending);
            Assert.False(queue.Reader.TryRead(out _), "replacement channel is empty");
        }

        [Fact]
        public void Enqueue_AfterCompletion_StartsAFreshBatch()
        {
            var queue = CreateQueue();
            queue.Enqueue(new[] { 1 });
            queue.MarkStarted(1, null);
            queue.MarkCompleted(1, true, 1);
            queue.MarkBatchFinishedIfDrained();
            Assert.False(queue.Snapshot().IsRunning);

            var result = queue.Enqueue(new[] { 1, 2 });

            var snapshot = queue.Snapshot();
            Assert.Equal(2, result.Accepted);
            Assert.True(snapshot.IsRunning);
            Assert.Equal(0, snapshot.Processed);
            Assert.Equal(0, snapshot.Grabbed);
            Assert.Equal(2, snapshot.Total);
            Assert.Null(snapshot.CompletedAt);
            Assert.False(snapshot.Cancelled);
        }

        [Fact]
        public void Enqueue_AfterCancel_UsesAFreshToken_AndIgnoresTheStaleInFlightBook()
        {
            var queue = CreateQueue();
            queue.Enqueue(new[] { 1, 2 });
            queue.MarkStarted(1, null);
            queue.Cancel();

            queue.Enqueue(new[] { 3 });
            Assert.False(queue.BatchToken.IsCancellationRequested);
            Assert.True(queue.Reader.TryRead(out var next));
            Assert.Equal(3, next);

            // The abandoned book from the cancelled batch reports in late.
            queue.MarkCompleted(1, success: false, downloadsQueued: 0);

            var snapshot = queue.Snapshot();
            Assert.Equal(0, snapshot.Processed);
            Assert.Equal(0, snapshot.Failed);
            Assert.Equal(1, snapshot.Total);
        }
    }
}
