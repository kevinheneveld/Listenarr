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

namespace Listenarr.Tests.Features.Infrastructure.HostedServices
{
    [Trait("Name", "WantedSearchBackgroundServiceTests")]
    [Trait("Category", "Search")]
    public class WantedSearchBackgroundServiceTests : BaseTests
    {
        private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(10);

        private static (WantedSearchBackgroundService Service, WantedSearchQueueService Queue, Mock<IAutomaticSearchInvoker> Invoker)
            Build(Func<int, CancellationToken, Task<AutomaticSearchBookResult>> searchBehavior)
        {
            var invoker = new Mock<IAutomaticSearchInvoker>();
            invoker
                .Setup(i => i.SearchAudiobookNowAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .Returns<int, CancellationToken>(searchBehavior);

            var audiobooks = new Mock<IAudiobookRepository>();
            audiobooks
                .Setup(r => r.GetByIdAsync(It.IsAny<int>()))
                .Returns<int>(id => Task.FromResult<Audiobook?>(new Audiobook { Id = id, Title = $"Book {id}" }));

            var services = new ServiceCollection();
            services.AddSingleton(audiobooks.Object);
            var provider = services.BuildServiceProvider();

            var queue = new WantedSearchQueueService(NullLogger<WantedSearchQueueService>.Instance);
            var service = new WantedSearchBackgroundService(
                queue,
                invoker.Object,
                provider.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<WantedSearchBackgroundService>.Instance,
                hubBroadcaster: null,
                bookDelayOverride: TimeSpan.Zero);

            return (service, queue, invoker);
        }

        private static AutomaticSearchBookResult Ok(int id, int queued) =>
            new() { AudiobookId = id, Title = $"Book {id}", Success = true, DownloadsQueued = queued };

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
        public async Task ProcessesQueuedBooksInOrder_AndSumsGrabs()
        {
            var seen = new List<int>();
            var (service, queue, invoker) = Build((id, _) =>
            {
                lock (seen) seen.Add(id);
                return Task.FromResult(Ok(id, id == 2 ? 0 : 1));
            });

            queue.Enqueue(new[] { 1, 2, 3 });
            await service.StartAsync(CancellationToken.None);
            try
            {
                await WaitUntilAsync(() => !queue.Snapshot().IsRunning);
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }

            var snapshot = queue.Snapshot();
            Assert.Equal(new[] { 1, 2, 3 }, seen);
            Assert.Equal(3, snapshot.Processed);
            Assert.Equal(2, snapshot.Grabbed);
            Assert.Equal(0, snapshot.Failed);
            Assert.NotNull(snapshot.CompletedAt);
            invoker.Verify(i => i.SearchAudiobookNowAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Exactly(3));
        }

        [Fact]
        public async Task OneFailingBook_IsCountedAsFailed_AndTheRestStillComplete()
        {
            var (service, queue, _) = Build((id, _) =>
                id == 2
                    ? throw new InvalidOperationException("indexer exploded")
                    : Task.FromResult(Ok(id, 1)));

            queue.Enqueue(new[] { 1, 2, 3 });
            await service.StartAsync(CancellationToken.None);
            try
            {
                await WaitUntilAsync(() => !queue.Snapshot().IsRunning);
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }

            var snapshot = queue.Snapshot();
            Assert.Equal(3, snapshot.Processed);
            Assert.Equal(1, snapshot.Failed);
            Assert.Equal(2, snapshot.Grabbed);
        }

        [Fact]
        public async Task InvokerReportedFailure_CountsAsFailed()
        {
            var (service, queue, _) = Build((id, _) => Task.FromResult(new AutomaticSearchBookResult
            {
                AudiobookId = id,
                Success = id != 1,
                DownloadsQueued = 0,
                Message = id == 1 ? "Audiobook not found" : null
            }));

            queue.Enqueue(new[] { 1, 2 });
            await service.StartAsync(CancellationToken.None);
            try
            {
                await WaitUntilAsync(() => !queue.Snapshot().IsRunning);
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }

            Assert.Equal(2, queue.Snapshot().Processed);
            Assert.Equal(1, queue.Snapshot().Failed);
        }

        [Fact]
        public async Task Cancel_StopsTheBatch_AndANewBatchStillRuns()
        {
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var (service, queue, _) = Build(async (id, _) =>
            {
                Interlocked.Increment(ref calls);
                if (id == 1)
                {
                    await release.Task;
                }
                return Ok(id, 1);
            });

            queue.Enqueue(new[] { 1, 2, 3 });
            await service.StartAsync(CancellationToken.None);
            try
            {
                await WaitUntilAsync(() => queue.Snapshot().CurrentAudiobookId == 1);

                queue.Cancel();
                release.SetResult();

                await WaitUntilAsync(() => queue.Snapshot().CurrentAudiobookId == null);
                Assert.True(queue.Snapshot().Cancelled);
                Assert.False(queue.Snapshot().IsRunning);

                queue.Enqueue(new[] { 4 });
                await WaitUntilAsync(() => !queue.Snapshot().IsRunning && queue.Snapshot().Processed == 1);
            }
            finally
            {
                await service.StopAsync(CancellationToken.None);
            }

            // Books 2 and 3 were dropped by the cancel; only 1 (abandoned) and 4 ran.
            Assert.Equal(2, calls);
            Assert.False(queue.Snapshot().Cancelled);
            Assert.Equal(1, queue.Snapshot().Grabbed);
        }
    }
}
