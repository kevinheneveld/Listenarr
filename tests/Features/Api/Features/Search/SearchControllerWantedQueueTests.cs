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
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Api.Features.Search
{
    [Trait("Name", "SearchControllerWantedQueueTests")]
    [Trait("Category", "Search")]
    public class SearchControllerWantedQueueTests : BaseTests
    {
        private static SearchController CreateController(IWantedSearchQueue? queue)
        {
            var controller = new SearchController(
                Mock.Of<ISearchService>(),
                NullLogger<SearchController>.Instance,
                new TestEmptyAudibleService(),
                Mock.Of<IAudiobookMetadataService>(),
                wantedSearchQueue: queue);
            controller.ControllerContext = new ControllerContext { HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext() };
            return controller;
        }

        private static WantedSearchQueueService CreateQueue() =>
            new(NullLogger<WantedSearchQueueService>.Instance);

        [Fact]
        public void Enqueue_WithNoIds_IsBadRequest()
        {
            var controller = CreateController(CreateQueue());

            var result = controller.EnqueueWantedSearch(new SearchController.WantedSearchQueueRequest { AudiobookIds = new List<int> { 0, -3 } });

            Assert.IsType<BadRequestObjectResult>(result.Result);
        }

        [Fact]
        public void Enqueue_WithoutQueue_IsServiceUnavailable()
        {
            var controller = CreateController(null);

            var result = controller.EnqueueWantedSearch(new SearchController.WantedSearchQueueRequest { AudiobookIds = new List<int> { 1 } });

            var status = Assert.IsType<ObjectResult>(result.Result);
            Assert.Equal(503, status.StatusCode);
        }

        [Fact]
        public void Enqueue_QueuesDistinctIds_AndReturnsAccepted()
        {
            var queue = CreateQueue();
            var controller = CreateController(queue);

            var result = controller.EnqueueWantedSearch(new SearchController.WantedSearchQueueRequest { AudiobookIds = new List<int> { 5, 5, 6 } });

            var accepted = Assert.IsType<AcceptedResult>(result.Result);
            var body = Assert.IsType<SearchController.WantedSearchEnqueueResponse>(accepted.Value);
            Assert.Equal(2, body.Accepted);
            Assert.Equal(0, body.AlreadyQueued);
            Assert.True(body.Snapshot.IsRunning);
            Assert.Equal(2, body.Snapshot.Pending);
            Assert.Equal(2, queue.Snapshot().Total);
        }

        [Fact]
        public void Get_ReturnsTheLiveSnapshot_OrIdleWithoutAQueue()
        {
            var queue = CreateQueue();
            queue.Enqueue(new[] { 9 });

            var live = CreateController(queue).GetWantedSearchQueue();
            var idle = CreateController(null).GetWantedSearchQueue();

            var liveSnapshot = Assert.IsType<WantedSearchQueueSnapshot>(Assert.IsType<OkObjectResult>(live.Result).Value);
            Assert.True(liveSnapshot.IsRunning);
            Assert.Equal(1, liveSnapshot.Pending);
            var idleSnapshot = Assert.IsType<WantedSearchQueueSnapshot>(Assert.IsType<OkObjectResult>(idle.Result).Value);
            Assert.False(idleSnapshot.IsRunning);
        }

        [Fact]
        public void Delete_CancelsTheBatch()
        {
            var queue = CreateQueue();
            queue.Enqueue(new[] { 1, 2 });

            var result = CreateController(queue).CancelWantedSearch();

            var snapshot = Assert.IsType<WantedSearchQueueSnapshot>(Assert.IsType<OkObjectResult>(result.Result).Value);
            Assert.True(snapshot.Cancelled);
            Assert.False(snapshot.IsRunning);
            Assert.Equal(0, snapshot.Pending);
        }
    }
}
