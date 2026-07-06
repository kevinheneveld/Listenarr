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
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Listenarr.Application.Audiobooks.Verification;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Api.Features.Verification
{
    [Trait("Area", "Verification")]
    [Trait("Name", "VerificationQueueStatusTests")]
    public class VerificationQueueStatusTests : BaseTests
    {
        private static JsonElement ToJson(object? value) => JsonSerializer.SerializeToElement(value);

        [Fact]
        public async Task EmptyQueue_ReportsQuietState()
        {
            var controller = _provider.GetRequiredService<Listenarr.Api.Features.Verification.VerificationController>();

            var result = await controller.GetQueueStatus(CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = ToJson(ok.Value);
            var verification = payload.GetProperty("verification");
            Assert.Equal(0, verification.GetProperty("queuedJobs").GetInt32());
            Assert.Equal(0, verification.GetProperty("queuedBooks").GetInt32());
            Assert.Equal(JsonValueKind.Null, verification.GetProperty("processing").ValueKind);
            Assert.Equal(JsonValueKind.Null, verification.GetProperty("etaSeconds").ValueKind);
            // No completed history: the fallback pace is reported, not zero.
            Assert.Equal(VerificationEtaMath.FallbackSecondsPerBook,
                verification.GetProperty("avgSecondsPerBook").GetDouble());
        }

        [Fact]
        public async Task QueuedJobs_ProduceBookCountsAndEta()
        {
            var queue = _provider.GetRequiredService<ILibraryVerificationQueueService>();
            await queue.EnqueueAsync(new List<int> { 1, 2, 3 });
            await queue.EnqueueAsync(new List<int> { 4, 5 });

            var controller = _provider.GetRequiredService<Listenarr.Api.Features.Verification.VerificationController>();
            var result = await controller.GetQueueStatus(CancellationToken.None);

            var payload = ToJson(Assert.IsType<OkObjectResult>(result).Value);
            var verification = payload.GetProperty("verification");
            Assert.Equal(2, verification.GetProperty("queuedJobs").GetInt32());
            Assert.Equal(5, verification.GetProperty("queuedBooks").GetInt32());
            // ETA = books × avg (fallback pace with no history).
            Assert.Equal(5 * VerificationEtaMath.FallbackSecondsPerBook,
                verification.GetProperty("etaSeconds").GetDouble());
        }

        [Fact]
        public async Task CompletedToday_CountsBooksVerifiedToday()
        {
            // Two books verified today, one yesterday — only today's count.
            // Anchored to UTC midnight (not relative offsets) so a test run just
            // after 00:00 UTC can't have "an hour ago" land on yesterday.
            await AddBookVerifiedAtAsync("Today A", DateTime.UtcNow);
            await AddBookVerifiedAtAsync("Today B", DateTime.UtcNow.Date.AddSeconds(1));
            await AddBookVerifiedAtAsync("Yesterday", DateTime.UtcNow.Date.AddHours(-2));

            var controller = _provider.GetRequiredService<Listenarr.Api.Features.Verification.VerificationController>();
            var result = await controller.GetQueueStatus(CancellationToken.None);

            var payload = ToJson(Assert.IsType<OkObjectResult>(result).Value);
            Assert.Equal(2, payload.GetProperty("verification").GetProperty("completedBooksToday").GetInt32());
        }

        [Fact]
        public async Task CompletedToday_CountsBooksInsideStillProcessingJob()
        {
            // The live bug: a 1,000-book job grinding for days reported zero
            // "done today" because only COMPLETED durable rows were counted.
            // Books get VerifiedAt stamped one by one as the job runs — the
            // metric must reflect that even while the job row says Processing.
            var jobRepo = _provider.GetRequiredService<IVerificationJobRepository>();
            await jobRepo.AddAsync(new VerificationJobRecord
            {
                Id = Guid.NewGuid(),
                AudiobookIdsJson = null, // whole-library job
                Status = "Processing",
                EnqueuedAt = DateTime.UtcNow.AddDays(-2)
            });
            await AddBookVerifiedAtAsync("Verified mid-job", DateTime.UtcNow);

            var controller = _provider.GetRequiredService<Listenarr.Api.Features.Verification.VerificationController>();
            var result = await controller.GetQueueStatus(CancellationToken.None);

            var payload = ToJson(Assert.IsType<OkObjectResult>(result).Value);
            Assert.Equal(1, payload.GetProperty("verification").GetProperty("completedBooksToday").GetInt32());
        }

        private async Task AddBookVerifiedAtAsync(string title, DateTime verifiedAtUtc)
        {
            var book = new Listenarr.Tests.Builders.AudiobookBuilder().WithTitle(title).Build();
            book.VerifiedAt = verifiedAtUtc;
            await _audiobookRepository.AddAsync(book);
        }
    }
}
