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
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Audiobooks.Organizing
{
    [Trait("Name", "OrganizeApplyBatchServiceTests")]
    [Trait("Category", "Library")]
    public class OrganizeApplyBatchServiceTests : BaseTests
    {
        private static OrganizeApplyBatchService CreateBatch() =>
            new(NullLogger<OrganizeApplyBatchService>.Instance);

        private static OrganizeApplyItem Item(int id) =>
            new(id, $"Book {id}", $"/src/{id}", $"/dst/{id}", ReplaceStubTarget: id % 2 == 0);

        private static List<OrganizeApplyItem> Drain(IOrganizeApplyBatch batch)
        {
            var items = new List<OrganizeApplyItem>();
            while (batch.Reader.TryRead(out var item))
            {
                items.Add(item);
            }
            return items;
        }

        [Fact]
        public void TryStart_QueuesEveryItemInOrder_AndReportsRunning()
        {
            var batch = CreateBatch();

            Assert.True(batch.TryStart(new[] { Item(1), Item(2), Item(3) }, out var batchId));

            Assert.NotEqual(Guid.Empty, batchId);
            var snapshot = batch.Snapshot();
            Assert.True(snapshot.IsRunning);
            Assert.Equal(batchId, snapshot.BatchId);
            Assert.Equal(3, snapshot.Total);
            Assert.Equal(0, snapshot.Processed);
            Assert.NotNull(snapshot.StartedAt);
            Assert.Equal(new[] { 1, 2, 3 }, Drain(batch).Select(i => i.AudiobookId));
        }

        [Fact]
        public void TryStart_WhileRunning_IsRefused()
        {
            var batch = CreateBatch();
            Assert.True(batch.TryStart(new[] { Item(1) }, out var first));

            Assert.False(batch.TryStart(new[] { Item(2) }, out var second));

            Assert.Equal(Guid.Empty, second);
            Assert.Equal(first, batch.Snapshot().BatchId);
            Assert.Equal(1, batch.Snapshot().Total);
        }

        [Fact]
        public void TryStart_EmptyList_FinishesImmediately()
        {
            var batch = CreateBatch();

            Assert.True(batch.TryStart(Array.Empty<OrganizeApplyItem>(), out _));

            var snapshot = batch.Snapshot();
            Assert.False(snapshot.IsRunning);
            Assert.NotNull(snapshot.CompletedAt);
        }

        [Fact]
        public void Marks_AccountQueuedNotAcceptedAndFailed_AndFinishWhenDrained()
        {
            var batch = CreateBatch();
            batch.TryStart(new[] { Item(1), Item(2), Item(3) }, out _);
            var items = Drain(batch);
            var jobId = Guid.NewGuid();

            batch.MarkStarted(items[0]);
            Assert.Equal(1, batch.Snapshot().CurrentAudiobookId);
            Assert.Equal("Book 1", batch.Snapshot().CurrentTitle);
            batch.MarkQueued(items[0], jobId);
            batch.MarkBatchFinishedIfDrained();
            Assert.True(batch.Snapshot().IsRunning);

            batch.MarkStarted(items[1]);
            batch.MarkNotAccepted(items[1], "Target directory already exists");
            batch.MarkBatchFinishedIfDrained();

            batch.MarkStarted(items[2]);
            batch.MarkFailed(items[2], "Failed to enqueue: boom");
            batch.MarkBatchFinishedIfDrained();

            var snapshot = batch.Snapshot();
            Assert.False(snapshot.IsRunning);
            Assert.Equal(3, snapshot.Processed);
            Assert.Equal(1, snapshot.Queued);
            Assert.Equal(1, snapshot.NotAccepted);
            Assert.Equal(1, snapshot.Failed);
            Assert.Null(snapshot.CurrentAudiobookId);
            Assert.NotNull(snapshot.CompletedAt);
            var queued = Assert.Single(snapshot.QueuedJobs);
            Assert.Equal(jobId.ToString(), queued.JobId);
            Assert.Equal("/dst/1", queued.TargetPath);
            Assert.Equal(2, snapshot.Problems.Count);
            Assert.Contains(snapshot.Problems, p => p.AudiobookId == 2 && p.Reason.Contains("already exists"));
            Assert.Contains(snapshot.Problems, p => p.AudiobookId == 3 && p.Reason.Contains("boom"));
        }

        [Fact]
        public void Cancel_DropsPendingItems_CancelsToken_AndAllowsANewBatch()
        {
            var batch = CreateBatch();
            batch.TryStart(new[] { Item(1), Item(2), Item(3) }, out var first);
            var token = batch.BatchToken;
            batch.Reader.TryRead(out var inFlight);
            batch.MarkStarted(inFlight!);

            batch.Cancel();

            var snapshot = batch.Snapshot();
            Assert.False(snapshot.IsRunning);
            Assert.True(snapshot.Cancelled);
            Assert.True(token.IsCancellationRequested);
            Assert.Empty(Drain(batch));

            Assert.True(batch.TryStart(new[] { Item(9) }, out var second));
            Assert.NotEqual(first, second);
            Assert.False(batch.BatchToken.IsCancellationRequested);
            Assert.False(batch.Snapshot().Cancelled);
            Assert.Equal(new[] { 9 }, Drain(batch).Select(i => i.AudiobookId));
        }

        [Fact]
        public void Snapshot_KeepsOnlyTheNewestProblems()
        {
            var batch = CreateBatch();
            var items = Enumerable.Range(1, OrganizeApplyBatchService.MaxProblemsInSnapshot + 5).Select(Item).ToList();
            batch.TryStart(items, out _);
            Drain(batch);

            foreach (var item in items)
            {
                batch.MarkStarted(item);
                batch.MarkNotAccepted(item, $"reason {item.AudiobookId}");
            }

            var snapshot = batch.Snapshot();
            Assert.Equal(items.Count, snapshot.NotAccepted);
            Assert.Equal(OrganizeApplyBatchService.MaxProblemsInSnapshot, snapshot.Problems.Count);
            Assert.Equal(items[^1].AudiobookId, snapshot.Problems[^1].AudiobookId);
            Assert.DoesNotContain(snapshot.Problems, p => p.AudiobookId == 1);
        }
    }
}
