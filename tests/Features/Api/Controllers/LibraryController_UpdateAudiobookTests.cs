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
using Listenarr.Api.Controllers;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Domain.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Api.Controllers
{
    [Trait("Area", "LibraryApi")]
    [Trait("Name", "LibraryController_UpdateAudiobookTests")]
    [Trait("Category", "LibraryController")]
    public class LibraryController_UpdateAudiobookTests : BaseTests
    {
        [Fact]
        [Trait("Method", "UpdateAudiobook")]
        [Trait("Scenario", "PersistsExpandedMetadataFields")]
        public async Task UpdateAudiobook_PersistsExpandedMetadataFields()
        {
            // Given
            var existingAudiobook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Original Title",
                Subtitle = "Original Subtitle",
                Authors = new List<string> { "Original Author" },
                Narrators = new List<string> { "Original Narrator" },
                Description = "Original description",
                Publisher = "Original Publisher",
                Language = "english",
                PublishedDate = "2024-01-01",
                PublishYear = "2024",
                Runtime = 600,
                Edition = "Original Edition",
                Version = "Original Version",
                Series = "Original Series",
                SeriesNumber = "1",
                SeriesMemberships = new List<AudiobookSeriesMembership>
                {
                    new()
                    {
                        SeriesName = "Original Series",
                        SeriesNumber = "1",
                        IsPrimary = true,
                        SortOrder = 0
                    }
                },
                Genres = new List<string> { "Fantasy" },
                ImageUrl = "https://example.com/original.jpg",
                Tags = new List<string> { "tag-one" },
                Monitored = true,
                Explicit = false,
                Abridged = false,
            });

            var controller = _provider.GetRequiredService<LibraryController>();

            var updatedAudiobook = new Audiobook
            {
                Title = "Edited Title",
                Subtitle = "Edited Subtitle",
                Authors = new List<string> { "Edited Author" },
                Narrators = new List<string> { "Edited Narrator" },
                Description = "Edited description",
                Publisher = "Edited Publisher",
                Language = "swedish",
                PublishedDate = "2025-02-01",
                PublishYear = "2025",
                Runtime = 720,
                Edition = "Collector Edition",
                Version = "Edited Version",
                Series = "Edited Universe",
                SeriesNumber = "4",
                SeriesMemberships = new List<AudiobookSeriesMembership>
                {
                    new()
                    {
                        SeriesName = "Edited Universe",
                        SeriesNumber = "4",
                        IsPrimary = true,
                        SortOrder = 0
                    },
                    new()
                    {
                        SeriesName = "Anthology Line",
                        SeriesNumber = "12",
                        IsPrimary = false,
                        SortOrder = 1
                    }
                },
                Genres = new List<string> { "Sci-Fi", "Adventure" },
                ImageUrl = "https://example.com/edited.jpg",
                Tags = new List<string> { "tag-two" },
                Monitored = false,
                Explicit = true,
                Abridged = true,
            };

            // When
            var actionResult = await controller.UpdateAudiobook(existingAudiobook.Id, updatedAudiobook);

            // Then
            Assert.IsType<OkObjectResult>(actionResult);
            var storedAudiobook = await _audiobookRepository.GetByIdAsync(existingAudiobook.Id);
            Assert.NotNull(storedAudiobook);
            Assert.Equal("Edited Title", storedAudiobook.Title);
            Assert.Equal("Edited Subtitle", storedAudiobook.Subtitle);
            Assert.Equal(new List<string> { "Edited Author" }, storedAudiobook.Authors);
            Assert.Equal(new List<string> { "Edited Narrator" }, storedAudiobook.Narrators);
            Assert.Equal("Edited description", storedAudiobook.Description);
            Assert.Equal("Edited Publisher", storedAudiobook.Publisher);
            Assert.Equal("swedish", storedAudiobook.Language);
            Assert.Equal("2025-02-01", storedAudiobook.PublishedDate);
            Assert.Equal("2025", storedAudiobook.PublishYear);
            Assert.Equal(720, storedAudiobook.Runtime);
            Assert.Equal("Collector Edition", storedAudiobook.Edition);
            Assert.Equal("Edited Version", storedAudiobook.Version);
            Assert.Equal("Edited Universe", storedAudiobook.Series);
            Assert.Equal("4", storedAudiobook.SeriesNumber);
            Assert.NotNull(storedAudiobook.SeriesMemberships);
            Assert.Collection(
                storedAudiobook.SeriesMemberships!,
                membership =>
                {
                    Assert.Equal("Edited Universe", membership.SeriesName);
                    Assert.Equal("4", membership.SeriesNumber);
                    Assert.True(membership.IsPrimary);
                },
                membership =>
                {
                    Assert.Equal("Anthology Line", membership.SeriesName);
                    Assert.Equal("12", membership.SeriesNumber);
                    Assert.False(membership.IsPrimary);
                });
            Assert.Equal(new List<string> { "Sci-Fi", "Adventure" }, storedAudiobook.Genres);
            Assert.Equal("https://example.com/edited.jpg", storedAudiobook.ImageUrl);
            Assert.Equal(new List<string> { "tag-two" }, storedAudiobook.Tags);
            Assert.False(storedAudiobook.Monitored);
            Assert.True(storedAudiobook.Explicit);
            Assert.True(storedAudiobook.Abridged);
        }

        // ── cacheImageLocally branch ───────────────────────────────────────
        //
        // PUT /library/{id}?cacheImageLocally=true is the contract used by the
        // metadata-backfill modal (always) and the edit modal's "Cache cover
        // art locally on save" checkbox (when checked). When set, an external
        // http(s) ImageUrl change should be downloaded into library storage
        // and the stored ImageUrl rewritten to the local path. When not set,
        // the external URL must persist verbatim.

        private static LibraryController BuildController(
            Audiobook existing,
            Mock<IImageCacheService> mockImageCache,
            Mock<IAudiobookRepository> mockRepo)
        {
            var mockLogger = new Mock<ILogger<LibraryController>>();
            var mockFileNaming = new Mock<IFileNamingService>();
            var services = new ServiceCollection();
            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
            return new LibraryController(
                mockRepo.Object,
                mockImageCache.Object,
                mockLogger.Object,
                scopeFactory,
                new Mock<IHistoryRepository>().Object,
                new Mock<IAudiobookFileRepository>().Object,
                new Mock<IQualityProfileRepository>().Object,
                new Mock<IDownloadRepository>().Object,
                new Mock<IRootFolderRepository>().Object,
                mockFileNaming.Object,
                new Mock<IApplicationPathService>().Object,
                new Mock<ILibraryListService>().Object);
        }

        [Fact]
        public async Task UpdateAudiobook_CachesExternalImage_WhenFlagIsTrue()
        {
            var existing = new Audiobook
            {
                Id = 7,
                Title = "T",
                Asin = "B00OLD",
                ImageUrl = "https://example.com/old.jpg"
            };
            var mockRepo = new Mock<IAudiobookRepository>();
            mockRepo.Setup(r => r.GetByIdAsync(7)).ReturnsAsync(existing);
            mockRepo.Setup(r => r.UpdateAsync(It.IsAny<Audiobook>())).ReturnsAsync(true);

            var mockImageCache = new Mock<IImageCacheService>();
            mockImageCache
                .Setup(c => c.MoveToLibraryStorageAsync("B00OLD", "https://m.media-amazon.com/images/I/abc.jpg"))
                .ReturnsAsync("config/cache/images/library/B00OLD.jpg");

            var controller = BuildController(existing, mockImageCache, mockRepo);
            var updated = new Audiobook { ImageUrl = "https://m.media-amazon.com/images/I/abc.jpg" };

            var result = await controller.UpdateAudiobook(7, updated, cacheImageLocally: true);

            Assert.IsType<OkObjectResult>(result);
            Assert.Equal("/config/cache/images/library/B00OLD.jpg", existing.ImageUrl);
            mockImageCache.Verify(c => c.MoveToLibraryStorageAsync("B00OLD", "https://m.media-amazon.com/images/I/abc.jpg"), Times.Once);
        }

        [Fact]
        public async Task UpdateAudiobook_DoesNotCacheImage_WhenFlagIsFalse()
        {
            var existing = new Audiobook
            {
                Id = 7,
                Title = "T",
                Asin = "B00OLD",
                ImageUrl = "https://example.com/old.jpg"
            };
            var mockRepo = new Mock<IAudiobookRepository>();
            mockRepo.Setup(r => r.GetByIdAsync(7)).ReturnsAsync(existing);
            mockRepo.Setup(r => r.UpdateAsync(It.IsAny<Audiobook>())).ReturnsAsync(true);

            var mockImageCache = new Mock<IImageCacheService>();
            var controller = BuildController(existing, mockImageCache, mockRepo);
            var updated = new Audiobook { ImageUrl = "https://m.media-amazon.com/images/I/abc.jpg" };

            await controller.UpdateAudiobook(7, updated, cacheImageLocally: false);

            // External URL persists verbatim, cache service never called.
            Assert.Equal("https://m.media-amazon.com/images/I/abc.jpg", existing.ImageUrl);
            mockImageCache.Verify(c => c.MoveToLibraryStorageAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task UpdateAudiobook_CachesUnchangedExternalImage_WhenFlagIsTrue()
        {
            // "Convert this existing external cover to local" is the primary
            // use case for the edit-modal checkbox: the user opens a record
            // whose cover is still hosted on Amazon, ticks the box (or leaves
            // it on its default-ON state for external URLs), and saves
            // unrelated edits. The URL didn't change but the user's *intent*
            // to cache did — honor that.
            //
            // The FE prevents this from re-downloading on every subsequent
            // save: once the URL is local, `canCacheImageLocally` returns
            // false, the checkbox hides, the flag stops being sent.
            var existing = new Audiobook
            {
                Id = 7,
                Title = "T",
                Asin = "B00OLD",
                ImageUrl = "https://example.com/old.jpg"
            };
            var mockRepo = new Mock<IAudiobookRepository>();
            mockRepo.Setup(r => r.GetByIdAsync(7)).ReturnsAsync(existing);
            mockRepo.Setup(r => r.UpdateAsync(It.IsAny<Audiobook>())).ReturnsAsync(true);

            var mockImageCache = new Mock<IImageCacheService>();
            mockImageCache
                .Setup(c => c.MoveToLibraryStorageAsync("B00OLD", "https://example.com/old.jpg"))
                .ReturnsAsync("config/cache/images/library/B00OLD.jpg");

            var controller = BuildController(existing, mockImageCache, mockRepo);
            // Same URL as existing — simulates the FE re-sending the current
            // value alongside an unrelated description edit. Cache must still
            // fire because cacheImageLocally=true is the user's signal.
            var updated = new Audiobook { ImageUrl = "https://example.com/old.jpg" };

            await controller.UpdateAudiobook(7, updated, cacheImageLocally: true);

            mockImageCache.Verify(c => c.MoveToLibraryStorageAsync("B00OLD", "https://example.com/old.jpg"), Times.Once);
            Assert.Equal("/config/cache/images/library/B00OLD.jpg", existing.ImageUrl);
        }

        [Fact]
        public async Task UpdateAudiobook_DoesNotCacheImage_WhenUrlIsAlreadyLocal()
        {
            var existing = new Audiobook
            {
                Id = 7,
                Title = "T",
                Asin = "B00OLD",
                ImageUrl = "https://example.com/old.jpg"
            };
            var mockRepo = new Mock<IAudiobookRepository>();
            mockRepo.Setup(r => r.GetByIdAsync(7)).ReturnsAsync(existing);
            mockRepo.Setup(r => r.UpdateAsync(It.IsAny<Audiobook>())).ReturnsAsync(true);

            var mockImageCache = new Mock<IImageCacheService>();
            var controller = BuildController(existing, mockImageCache, mockRepo);
            // Local cache-style URL — IsExternalHttpImageUrl returns false.
            var updated = new Audiobook { ImageUrl = "/config/cache/images/library/B00OLD.jpg" };

            await controller.UpdateAudiobook(7, updated, cacheImageLocally: true);

            Assert.Equal("/config/cache/images/library/B00OLD.jpg", existing.ImageUrl);
            mockImageCache.Verify(c => c.MoveToLibraryStorageAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task UpdateAudiobook_CachesUnderNewAsin_WhenAsinAndImageChangeInSamePut()
        {
            // The backfill modal sends a new ASIN *and* a new ImageUrl in the
            // same PUT. The cache key must come from the post-merge ASIN —
            // keying on the pre-merge ASIN would file the new cover under the
            // wrong identifier.
            var existing = new Audiobook
            {
                Id = 7,
                Title = "T",
                Asin = "B00OLD",
                ImageUrl = "https://example.com/old.jpg"
            };
            var mockRepo = new Mock<IAudiobookRepository>();
            mockRepo.Setup(r => r.GetByIdAsync(7)).ReturnsAsync(existing);
            mockRepo.Setup(r => r.UpdateAsync(It.IsAny<Audiobook>())).ReturnsAsync(true);

            var mockImageCache = new Mock<IImageCacheService>();
            mockImageCache
                .Setup(c => c.MoveToLibraryStorageAsync("B00NEW", "https://m.media-amazon.com/images/I/new.jpg"))
                .ReturnsAsync("config/cache/images/library/B00NEW.jpg");

            var controller = BuildController(existing, mockImageCache, mockRepo);
            var updated = new Audiobook
            {
                Asin = "B00NEW",
                ImageUrl = "https://m.media-amazon.com/images/I/new.jpg"
            };

            await controller.UpdateAudiobook(7, updated, cacheImageLocally: true);

            mockImageCache.Verify(c => c.MoveToLibraryStorageAsync("B00NEW", "https://m.media-amazon.com/images/I/new.jpg"), Times.Once);
            mockImageCache.Verify(c => c.MoveToLibraryStorageAsync("B00OLD", It.IsAny<string>()), Times.Never);
            Assert.Equal("/config/cache/images/library/B00NEW.jpg", existing.ImageUrl);
        }

        [Fact]
        public async Task UpdateAudiobook_FallsBackToExternalUrl_WhenCacheServiceReturnsNull()
        {
            var existing = new Audiobook
            {
                Id = 7,
                Title = "T",
                Asin = "B00OLD",
                ImageUrl = "https://example.com/old.jpg"
            };
            var mockRepo = new Mock<IAudiobookRepository>();
            mockRepo.Setup(r => r.GetByIdAsync(7)).ReturnsAsync(existing);
            mockRepo.Setup(r => r.UpdateAsync(It.IsAny<Audiobook>())).ReturnsAsync(true);

            var mockImageCache = new Mock<IImageCacheService>();
            mockImageCache
                .Setup(c => c.MoveToLibraryStorageAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync((string?)null);

            var controller = BuildController(existing, mockImageCache, mockRepo);
            var updated = new Audiobook { ImageUrl = "https://m.media-amazon.com/images/I/abc.jpg" };

            await controller.UpdateAudiobook(7, updated, cacheImageLocally: true);

            // Cache attempt failed — fall back to the external URL so the user
            // still sees a cover, matching the add-path's behavior.
            Assert.Equal("https://m.media-amazon.com/images/I/abc.jpg", existing.ImageUrl);
        }
    }
}
