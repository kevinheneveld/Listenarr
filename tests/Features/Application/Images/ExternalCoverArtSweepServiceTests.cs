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
using Listenarr.Application.Common.Images;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Images
{
    [Trait("Area", "Library")]
    [Trait("Name", "ExternalCoverArtSweepServiceTests")]
    public class ExternalCoverArtSweepServiceTests : BaseTests
    {
        private ExternalCoverArtSweepService CreateService(Mock<IImageCacheService> imageCache)
        {
            return new ExternalCoverArtSweepService(
                _audiobookRepository,
                imageCache.Object,
                NullLogger<ExternalCoverArtSweepService>.Instance);
        }

        private async Task<Audiobook> AddBookAsync(string title, string? imageUrl, string? asin = null)
        {
            var builder = new AudiobookBuilder().WithTitle(title);
            var book = builder.Build();
            book.ImageUrl = imageUrl;
            book.Asin = asin;
            return await _audiobookRepository.AddAsync(book);
        }

        [Theory]
        [Trait("Scenario", "ShouldCacheLocally")]
        [InlineData("https://m.media-amazon.com/images/I/x.jpg", true)]
        [InlineData("http://cdn.example.com/cover.png", true)]
        [InlineData("/cache/images/library/B00X.jpg", false)]
        [InlineData("/api/v1/images/B00X", false)]
        [InlineData("https://listenarr.example.com/api/v1/images/B00X", false)]
        [InlineData("https://host.example.com/config/cache/images/library/x.jpg", false)]
        [InlineData("https://host.example.com/cache/images/library/x.jpg", false)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void ShouldCacheLocally_ClassifiesUrls(string? url, bool expected)
        {
            Assert.Equal(expected, ExternalCoverArtSweepService.ShouldCacheLocally(url));
        }

        [Fact]
        [Trait("Scenario", "Sweep_CachesExternal_SkipsLocal_RewritesUrl")]
        public async Task Sweep_CachesExternal_SkipsLocal_RewritesUrl()
        {
            var external = await AddBookAsync("External", "https://m.media-amazon.com/images/I/ext.jpg", asin: "B00EXT");
            var local = await AddBookAsync("Local", "/cache/images/library/loc.jpg");
            var selfRoute = await AddBookAsync("SelfRoute", "https://listenarr.example.com/api/v1/images/B00Y");

            var imageCache = new Mock<IImageCacheService>();
            imageCache
                .Setup(c => c.MoveToLibraryStorageAsync("B00EXT", "https://m.media-amazon.com/images/I/ext.jpg"))
                .ReturnsAsync("cache/images/library/B00EXT.jpg");

            var result = await CreateService(imageCache).SweepAsync();

            Assert.Equal(3, result.TotalScanned);
            Assert.Equal(2, result.AlreadyLocal);
            Assert.Equal(1, result.Queued);
            Assert.Equal(1, result.Succeeded);
            Assert.Equal(0, result.Failed);

            var reloaded = await _audiobookRepository.GetByIdAsync(external.Id);
            Assert.Equal("/cache/images/library/B00EXT.jpg", reloaded!.ImageUrl);
            // Untouched records keep their URLs.
            Assert.Equal("/cache/images/library/loc.jpg", (await _audiobookRepository.GetByIdAsync(local.Id))!.ImageUrl);
            Assert.Equal("https://listenarr.example.com/api/v1/images/B00Y", (await _audiobookRepository.GetByIdAsync(selfRoute.Id))!.ImageUrl);
            imageCache.Verify(c => c.MoveToLibraryStorageAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Once);
        }

        [Fact]
        [Trait("Scenario", "Sweep_FailureContinues_KeepsExternalUrl")]
        public async Task Sweep_FailureContinues_KeepsExternalUrl()
        {
            var failing = await AddBookAsync("Failing", "https://cdn.example.com/fail.jpg", asin: "B00FAIL");
            var working = await AddBookAsync("Working", "https://cdn.example.com/ok.jpg", asin: "B00OK");

            var imageCache = new Mock<IImageCacheService>();
            // Failure path: cache service declines (null) for one record…
            imageCache
                .Setup(c => c.MoveToLibraryStorageAsync("B00FAIL", It.IsAny<string>()))
                .ReturnsAsync((string?)null);
            // …and succeeds for the other.
            imageCache
                .Setup(c => c.MoveToLibraryStorageAsync("B00OK", It.IsAny<string>()))
                .ReturnsAsync("cache/images/library/B00OK.jpg");

            var result = await CreateService(imageCache).SweepAsync();

            Assert.Equal(2, result.Queued);
            Assert.Equal(1, result.Succeeded);
            Assert.Equal(1, result.Failed);

            // Failed record keeps its external URL for a later retry.
            Assert.Equal("https://cdn.example.com/fail.jpg", (await _audiobookRepository.GetByIdAsync(failing.Id))!.ImageUrl);
            Assert.Equal("/cache/images/library/B00OK.jpg", (await _audiobookRepository.GetByIdAsync(working.Id))!.ImageUrl);
        }
    }
}
