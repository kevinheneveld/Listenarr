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
using Listenarr.Infrastructure.HostedServices.Library;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Infrastructure.HostedServices
{
    /// <summary>
    /// The worker that drains the organize batch into the move queue: every
    /// row is handed to the enqueuer once, outcomes are tallied, a cancel
    /// stops the feed, and one throwing row does not stop the rest.
    /// </summary>
    [Trait("Name", "OrganizeApplyBackgroundServiceTests")]
    [Trait("Category", "Library")]
    public class OrganizeApplyBackgroundServiceTests : BaseTests
    {
        private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(10);

        private static (OrganizeApplyBackgroundService Service, OrganizeApplyBatchService Batch, Mock<IOrganizeMoveEnqueuer> Enqueuer)
            Build(Func<OrganizeApplyItem, CancellationToken, Task<OrganizeMoveEnqueueOutcome>> behavior, bool registerEnqueuer = true)
        {
            var enqueuer = new Mock<IOrganizeMoveEnqueuer>();
            enqueuer
                .Setup(e => e.EnqueueAsync(It.IsAny<OrganizeApplyItem>(), It.IsAny<CancellationToken>()))
                .Returns<OrganizeApplyItem, CancellationToken>(behavior);

            var services = new ServiceCollection();
            if (registerEnqueuer)
            {
                services.AddSingleton(enqueuer.Object);
            }
            var provider = services.BuildServiceProvider();

            var batch = new OrganizeApplyBatchService(NullLogger<OrganizeApplyBatchService>.Instance);
            var service = new OrganizeApplyBackgroundService(
                batch,
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<OrganizeApplyBackgroundService>.Instance);
            return (service, batch, enqueuer);
        }

        private static OrganizeApplyItem Item(int id) => new(id, $"Book {id}", $"/src/{id}", $"/dst/{id}", false);

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            var deadline = DateTime.UtcNow + WaitBudget;
            while (!condition() && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }
            Assert.True(condition(), "condition not met within the wait budget");
        }

        [Fact]
        public async Task DrainsEveryRowInOrder_AndTalliesOutcomes()
        {
            var seen = new List<int>();
            var (service, batch, _) = Build((item, _) =>
            {
                lock (seen) seen.Add(item.AudiobookId);
                return Task.FromResult(item.AudiobookId switch
                {
                    2 => new OrganizeMoveEnqueueOutcome(false, null, "Target directory already exists on disk"),
                    3 => throw new InvalidOperationException("boom"),
                    _ => new OrganizeMoveEnqueueOutcome(true, Guid.NewGuid(), null),
                });
            });

            batch.TryStart(new[] { Item(1), Item(2), Item(3), Item(4) }, out _);
            await service.StartAsync(CancellationToken.None);
            try
            {
                await WaitUntilAsync(() => !batch.Snapshot().IsRunning);
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }

            Assert.Equal(new[] { 1, 2, 3, 4 }, seen);
            var snapshot = batch.Snapshot();
            Assert.Equal(4, snapshot.Processed);
            Assert.Equal(2, snapshot.Queued);
            Assert.Equal(1, snapshot.NotAccepted);
            Assert.Equal(1, snapshot.Failed);
            Assert.Equal(new[] { 1, 4 }, snapshot.QueuedJobs.Select(j => j.AudiobookId));
            Assert.Contains(snapshot.Problems, p => p.AudiobookId == 2 && p.Reason.Contains("already exists"));
            Assert.Contains(snapshot.Problems, p => p.AudiobookId == 3 && p.Reason.Contains("boom"));
        }

        [Fact]
        public async Task Cancel_StopsFeedingTheMoveQueue()
        {
            var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var (service, batch, _) = Build(async (item, ct) =>
            {
                Interlocked.Increment(ref calls);
                if (item.AudiobookId == 1)
                {
                    await gate.Task.WaitAsync(ct);
                }
                return new OrganizeMoveEnqueueOutcome(true, Guid.NewGuid(), null);
            });

            batch.TryStart(new[] { Item(1), Item(2), Item(3) }, out _);
            await service.StartAsync(CancellationToken.None);
            try
            {
                await WaitUntilAsync(() => Volatile.Read(ref calls) == 1);
                batch.Cancel();
                gate.TrySetResult(true);
                await Task.Delay(100);

                var snapshot = batch.Snapshot();
                Assert.False(snapshot.IsRunning);
                Assert.True(snapshot.Cancelled);
                Assert.Equal(1, Volatile.Read(ref calls));

                // A fresh batch runs on the replacement channel.
                batch.TryStart(new[] { Item(9) }, out _);
                await WaitUntilAsync(() => !batch.Snapshot().IsRunning);
                Assert.Equal(1, batch.Snapshot().Queued);
                Assert.Equal(2, Volatile.Read(ref calls));
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }
        }

        [Fact]
        public async Task NoEnqueuerRegistered_MarksEveryRowFailedInsteadOfCrashing()
        {
            var (service, batch, _) = Build((_, _) => Task.FromResult(new OrganizeMoveEnqueueOutcome(true, Guid.NewGuid(), null)), registerEnqueuer: false);

            batch.TryStart(new[] { Item(1), Item(2) }, out _);
            await service.StartAsync(CancellationToken.None);
            try
            {
                await WaitUntilAsync(() => !batch.Snapshot().IsRunning);
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }

            var snapshot = batch.Snapshot();
            Assert.Equal(2, snapshot.Failed);
            Assert.Equal(0, snapshot.Queued);
        }
    }
}
