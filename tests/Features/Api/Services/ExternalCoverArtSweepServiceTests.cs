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
using Listenarr.Application.Common;
using Listenarr.Application.Common.Images;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Domain.Models;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Api.Services
{
    public class ExternalCoverArtSweepServiceTests
    {
        private static ExternalCoverArtSweepService Build(
            List<Audiobook> books,
            Mock<IImageCacheService> imageCache,
            Mock<IAudiobookRepository>? repo = null)
        {
            var mockRepo = repo ?? new Mock<IAudiobookRepository>();
            mockRepo.Setup(r => r.GetAllAsync()).ReturnsAsync(books);
            mockRepo.Setup(r => r.UpdateAsync(It.IsAny<Audiobook>())).ReturnsAsync(true);
            return new ExternalCoverArtSweepService(
                mockRepo.Object,
                imageCache.Object,
                new Mock<ILogger<ExternalCoverArtSweepService>>().Object);
        }

        [Fact]
        public async Task Sweep_DownloadsExternalCover_AndRewritesUrlToLocalPath()
        {
            var book = new Audiobook
            {
                Id = 7,
                Title = "T",
                Asin = "B00ABC",
                ImageUrl = "https://m.media-amazon.com/images/I/abc.jpg",
            };
            var imageCache = new Mock<IImageCacheService>();
            imageCache
                .Setup(c => c.MoveToLibraryStorageAsync("B00ABC", book.ImageUrl))
                .ReturnsAsync("config/cache/images/library/B00ABC.jpg");
            var repo = new Mock<IAudiobookRepository>();
            repo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Audiobook> { book });
            repo.Setup(r => r.UpdateAsync(It.IsAny<Audiobook>())).ReturnsAsync(true);

            var svc = Build(new List<Audiobook> { book }, imageCache, repo);
            var result = await svc.SweepAsync();

            Assert.Equal(1, result.TotalScanned);
            Assert.Equal(0, result.AlreadyLocal);
            Assert.Equal(1, result.Queued);
            Assert.Equal(1, result.Succeeded);
            Assert.Equal(0, result.Failed);
            Assert.Equal("/config/cache/images/library/B00ABC.jpg", book.ImageUrl);
            repo.Verify(r => r.UpdateAsync(book), Times.Once);
        }

        [Fact]
        public async Task Sweep_SkipsRecordsWhoseUrlIsAlreadyLocal()
        {
            // All three of these should count as "already local" and trigger no
            // download or DB write: a path-style cache URL, a relative API URL,
            // and a fully-qualified URL that happens to point at our own cache
            // route (the stricter sweep predicate). Empty URLs are also skipped.
            var books = new List<Audiobook>
            {
                new() { Id = 1, ImageUrl = "/config/cache/images/library/B001.jpg" },
                new() { Id = 2, ImageUrl = "/api/v1/images/B002" },
                new() { Id = 3, ImageUrl = "https://listenarr.example.com/api/v1/images/B003" },
                new() { Id = 4, ImageUrl = "" },
                new() { Id = 5, ImageUrl = null },
            };
            var imageCache = new Mock<IImageCacheService>();
            var repo = new Mock<IAudiobookRepository>();
            repo.Setup(r => r.GetAllAsync()).ReturnsAsync(books);

            var svc = Build(books, imageCache, repo);
            var result = await svc.SweepAsync();

            Assert.Equal(5, result.TotalScanned);
            Assert.Equal(5, result.AlreadyLocal);
            Assert.Equal(0, result.Queued);
            Assert.Equal(0, result.Succeeded);
            Assert.Equal(0, result.Failed);
            imageCache.Verify(c => c.MoveToLibraryStorageAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            repo.Verify(r => r.UpdateAsync(It.IsAny<Audiobook>()), Times.Never);
        }

        [Fact]
        public async Task Sweep_IsIdempotent_WhenAllRecordsAlreadyCached()
        {
            // First pass caches everything. Second pass — same set, but with
            // the URLs rewritten — must download nothing and write nothing.
            var book = new Audiobook
            {
                Id = 7,
                Title = "T",
                Asin = "B00ABC",
                ImageUrl = "https://m.media-amazon.com/images/I/abc.jpg",
            };
            var imageCache = new Mock<IImageCacheService>();
            imageCache
                .Setup(c => c.MoveToLibraryStorageAsync("B00ABC", "https://m.media-amazon.com/images/I/abc.jpg"))
                .ReturnsAsync("config/cache/images/library/B00ABC.jpg");
            var repo = new Mock<IAudiobookRepository>();
            repo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Audiobook> { book });
            repo.Setup(r => r.UpdateAsync(It.IsAny<Audiobook>())).ReturnsAsync(true);

            var svc = Build(new List<Audiobook> { book }, imageCache, repo);
            await svc.SweepAsync();
            Assert.Equal("/config/cache/images/library/B00ABC.jpg", book.ImageUrl);

            var second = await svc.SweepAsync();
            Assert.Equal(1, second.AlreadyLocal);
            Assert.Equal(0, second.Queued);
            Assert.Equal(0, second.Succeeded);
            imageCache.Verify(c => c.MoveToLibraryStorageAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
            repo.Verify(r => r.UpdateAsync(It.IsAny<Audiobook>()), Times.Once);
        }

        [Fact]
        public async Task Sweep_LeavesExternalUrlAndSkipsWrite_WhenDownloadFails()
        {
            var book = new Audiobook
            {
                Id = 7,
                Asin = "B00ABC",
                ImageUrl = "https://m.media-amazon.com/images/I/abc.jpg",
            };
            var imageCache = new Mock<IImageCacheService>();
            imageCache
                .Setup(c => c.MoveToLibraryStorageAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync((string?)null);
            var repo = new Mock<IAudiobookRepository>();
            repo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Audiobook> { book });

            var svc = Build(new List<Audiobook> { book }, imageCache, repo);
            var result = await svc.SweepAsync();

            Assert.Equal(1, result.Queued);
            Assert.Equal(0, result.Succeeded);
            Assert.Equal(1, result.Failed);
            Assert.Equal("https://m.media-amazon.com/images/I/abc.jpg", book.ImageUrl);
            repo.Verify(r => r.UpdateAsync(It.IsAny<Audiobook>()), Times.Never);
        }

        [Fact]
        public async Task Sweep_ContinuesAfterFailure_AndCountsOthersIndependently()
        {
            var goodBook = new Audiobook
            {
                Id = 1,
                Asin = "B00OK",
                ImageUrl = "https://m.media-amazon.com/images/I/ok.jpg",
            };
            var badBook = new Audiobook
            {
                Id = 2,
                Asin = "B00BAD",
                ImageUrl = "https://m.media-amazon.com/images/I/bad.jpg",
            };
            var imageCache = new Mock<IImageCacheService>();
            imageCache
                .Setup(c => c.MoveToLibraryStorageAsync("B00BAD", It.IsAny<string>()))
                .ThrowsAsync(new HttpRequestException("boom"));
            imageCache
                .Setup(c => c.MoveToLibraryStorageAsync("B00OK", It.IsAny<string>()))
                .ReturnsAsync("config/cache/images/library/B00OK.jpg");
            var repo = new Mock<IAudiobookRepository>();
            repo.Setup(r => r.GetAllAsync()).ReturnsAsync(new List<Audiobook> { badBook, goodBook });
            repo.Setup(r => r.UpdateAsync(It.IsAny<Audiobook>())).ReturnsAsync(true);

            var svc = Build(new List<Audiobook> { badBook, goodBook }, imageCache, repo);
            var result = await svc.SweepAsync();

            Assert.Equal(2, result.Queued);
            Assert.Equal(1, result.Succeeded);
            Assert.Equal(1, result.Failed);
            Assert.Equal("https://m.media-amazon.com/images/I/bad.jpg", badBook.ImageUrl);
            Assert.Equal("/config/cache/images/library/B00OK.jpg", goodBook.ImageUrl);
        }

        [Fact]
        public async Task Sweep_KeysOnHashedFallback_WhenNoAsinAndNoIsbn()
        {
            // No ASIN, no ISBN — the helper must build a stable hash key from
            // title + first author so the cover file ends up somewhere
            // deterministic instead of getting a random Guid each run.
            var book = new Audiobook
            {
                Id = 9,
                Title = "Some Book",
                Authors = new List<string> { "An Author" },
                ImageUrl = "https://m.media-amazon.com/images/I/x.jpg",
            };
            string? capturedKey = null;
            var imageCache = new Mock<IImageCacheService>();
            imageCache
                .Setup(c => c.MoveToLibraryStorageAsync(It.IsAny<string>(), It.IsAny<string>()))
                .Callback<string, string?>((key, _) => capturedKey = key)
                .ReturnsAsync("config/cache/images/library/derived.jpg");

            var svc = Build(new List<Audiobook> { book }, imageCache);
            var result = await svc.SweepAsync();

            Assert.Equal(1, result.Succeeded);
            Assert.NotNull(capturedKey);
            Assert.StartsWith("img-", capturedKey);
            // Same inputs must produce the same key — anchor the determinism
            // expectation so a future refactor that swaps SHA-1 for Guid fails
            // here rather than silently breaking idempotency.
            var rebuilt = LibraryImageStorageHelper.BuildLibraryImageKey(book);
            Assert.Equal(rebuilt, capturedKey);
        }
    }
}
