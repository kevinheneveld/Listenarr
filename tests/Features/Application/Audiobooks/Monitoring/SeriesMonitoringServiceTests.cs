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
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Audiobooks.Monitoring
{
    [Trait("Name", "SeriesMonitoringServiceTests")]
    [Trait("Category", "SeriesMonitoringService")]
    public class SeriesMonitoringServiceTests : BaseTests
    {
        private readonly Mock<ISeriesCatalogService> _seriesCatalogService = new();
        private readonly Mock<ILibraryAddService> _libraryAddService = new();
        private readonly Mock<IImageCacheService> _imageCacheService = new();

        [Fact]
        public async Task MonitorSeriesAsync_PersistsSeriesAndAddsOnlyMissingBooksForSelectedLanguage()
        {
            // Given
            Init(services => services
                .WithSingleton(_seriesCatalogService.Object)
                .WithSingleton(_libraryAddService.Object));

            await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("The Final Empire")
                .WithAuthor("Brandon Sanderson")
                .WithSeries("Mistborn")
                .WithMonitored()
                .Build());

            _seriesCatalogService
                .Setup(service => service.GetCatalogAsync(
                    "Mistborn",
                    "uk",
                    500,
                    null,
                    true,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SeriesCatalogFetchResultBuilder()
                    .WithSeries("Mistborn", "SERIES123")
                    .WithBook(new AudibleSearchResultBuilder()
                        .WithTitle("The Final Empire")
                        .WithAuthor("Brandon Sanderson")
                        .WithLanguage("en-us")
                        .WithSeries("Mistborn", "1")
                        .Build())
                    .WithBook(new AudibleSearchResultBuilder()
                        .WithAsin("BOOK2")
                        .WithTitle("The Well of Ascension")
                        .WithAuthor("Brandon Sanderson")
                        .WithLanguage("english")
                        .WithSeries("Mistborn", "2")
                        .Build())
                    .WithBook(new AudibleSearchResultBuilder()
                        .WithAsin("BOOK3")
                        .WithTitle("Held der Zeiten")
                        .WithAuthor("Brandon Sanderson")
                        .WithLanguage("de")
                        .WithSeries("Mistborn", "3")
                        .Build())
                    .Build());

            _libraryAddService
                .Setup(service => service.AddToLibraryAsync(
                    It.Is<LibraryAddOperationRequest>(request =>
                        request.Metadata.Title == "The Well of Ascension" &&
                        request.Monitored &&
                        request.HistorySource == "SeriesMonitoring"),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new LibraryAddOperationResult
                {
                    Added = true,
                    Message = "Audiobook added to library successfully",
                    Audiobook = new AudiobookBuilder()
                        .WithTitle("The Well of Ascension")
                        .WithAuthor("Brandon Sanderson")
                        .WithSeries("Mistborn")
                        .WithMonitored()
                        .Build()
                });

            var service = _provider.GetRequiredService<ISeriesMonitoringService>();

            // When
            var result = await service.MonitorSeriesAsync(new MonitorSeriesRequest
            {
                Name = "Mistborn",
                Region = "uk",
                Language = "english"
            });

            // Then
            Assert.NotNull(result.MonitoredSeries);
            Assert.True(result.SyncResult.Succeeded);
            Assert.Equal(1, result.SyncResult.AddedCount);
            Assert.Equal(1, result.SyncResult.ExistingCount);
            Assert.Equal(0, result.SyncResult.FailedCount);
            Assert.Equal("SERIES123", result.MonitoredSeries!.SeriesAsin);
            Assert.Equal("uk", result.MonitoredSeries.Region);
            Assert.Equal("english", result.MonitoredSeries.Language);
            Assert.NotNull(result.MonitoredSeries.LastSuccessfulSyncAt);

            _libraryAddService.Verify(service => service.AddToLibraryAsync(
                    It.IsAny<LibraryAddOperationRequest>(),
                    It.IsAny<CancellationToken>()),
                Times.Once);

            var monitoredSeriesRepository = _provider.GetRequiredService<IMonitoredSeriesRepository>();
            var storedSeries = Assert.Single(await monitoredSeriesRepository.GetAllAsync());
            Assert.Equal("Mistborn", storedSeries.SeriesName);
            Assert.Equal("mistborn", storedSeries.SeriesNameNormalized);
            Assert.Equal("SERIES123", storedSeries.SeriesAsin);
        }

        [Fact]
        public async Task MonitorSeriesAsync_SkipsExcludedBooks_WithoutAddingThem()
        {
            // Given
            Init(services => services
                .WithSingleton(_seriesCatalogService.Object)
                .WithSingleton(_libraryAddService.Object));

            // The user deleted this book and kept it out of monitoring; the exclusion
            // list is shared between author and series monitoring.
            var exclusions = _provider.GetRequiredService<IAuthorMonitoringExclusionRepository>();
            await exclusions.AddAsync(new AuthorMonitoringExclusion
            {
                Asin = "BOOK2",
                Title = "The Well of Ascension",
                AuthorName = "Brandon Sanderson"
            });

            _seriesCatalogService
                .Setup(service => service.GetCatalogAsync(
                    "Mistborn",
                    "uk",
                    500,
                    null,
                    true,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SeriesCatalogFetchResultBuilder()
                    .WithSeries("Mistborn", "SERIES123")
                    .WithBook(new AudibleSearchResultBuilder()
                        .WithAsin("BOOK2")
                        .WithTitle("The Well of Ascension")
                        .WithAuthor("Brandon Sanderson")
                        .WithLanguage("english")
                        .WithSeries("Mistborn", "2")
                        .Build())
                    .Build());

            var service = _provider.GetRequiredService<ISeriesMonitoringService>();

            // When
            var result = await service.MonitorSeriesAsync(new MonitorSeriesRequest
            {
                Name = "Mistborn",
                Region = "uk",
                Language = "english"
            });

            // Then
            Assert.True(result.SyncResult.Succeeded);
            Assert.Equal(0, result.SyncResult.AddedCount);
            Assert.Equal(1, result.SyncResult.ExcludedCount);

            // The excluded book must never reach the library-add path.
            _libraryAddService.Verify(service => service.AddToLibraryAsync(
                    It.IsAny<LibraryAddOperationRequest>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task MonitorSeriesAsync_PersistsTitleFolderInBasePath()
        {
            // Given
            Init(services => services
                .WithSingleton(_seriesCatalogService.Object)
                .WithSingleton(_imageCacheService.Object));

            var rootPath = FileService.GetTempDirectory("series-monitoring-library");

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithFolderNamingPattern("{Author}/{Series}/{Title}")
                .Build());

            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithIsDefault()
                .WithPath(rootPath)
                .Build());

            _seriesCatalogService
                .Setup(service => service.GetCatalogAsync(
                    "Dungeon Crawler Carl",
                    "us",
                    500,
                    null,
                    true,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SeriesCatalogFetchResultBuilder()
                    .WithSeries("Dungeon Crawler Carl", "SERIES123")
                    .WithBook(new AudibleSearchResultBuilder()
                        .WithAsin("BOOK123")
                        .WithTitle("This Inevitable Ruin")
                        .WithAuthor("Matt Dinniman")
                        .WithLanguage("english")
                        .WithSeries("Dungeon Crawler Carl", "7", "SERIES123")
                        .Build())
                    .Build());

            var service = _provider.GetRequiredService<ISeriesMonitoringService>();

            // When
            var result = await service.MonitorSeriesAsync(new MonitorSeriesRequest
            {
                Name = "Dungeon Crawler Carl",
                Region = "us",
                Language = "english"
            });

            // Then
            Assert.True(result.SyncResult.Succeeded);
            Assert.Equal(1, result.SyncResult.AddedCount);

            var storedAudiobook = Assert.Single(await _audiobookRepository.GetAllAsync());
            Assert.Equal(
                Path.Join(rootPath, "Matt Dinniman", "Dungeon Crawler Carl", "This Inevitable Ruin"),
                storedAudiobook.BasePath);
        }
        [Fact]
        public async Task RepointSeriesAsync_PinsAsinAndResyncsByAsin()
        {
            // Given
            Init(services => services
                .WithSingleton(_seriesCatalogService.Object)
                .WithSingleton(_libraryAddService.Object));

            var repo = _provider.GetRequiredService<IMonitoredSeriesRepository>();
            var row = await repo.UpsertAsync(new MonitoredSeries
            {
                SeriesName = "Smugglers Tale",
                SeriesNameNormalized = "smugglers tale",
                SeriesAsin = "WRONGASIN",
                Region = "us",
                Language = "all"
            });

            _seriesCatalogService
                .Setup(service => service.GetCatalogByAsinAsync(
                    "Smugglers Tale",
                    "RIGHTASIN",
                    "us",
                    500,
                    null,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SeriesCatalogFetchResultBuilder()
                    .WithSeries("Smuggler's Tales", "RIGHTASIN")
                    .Build());

            var service = _provider.GetRequiredService<ISeriesMonitoringService>();

            // When
            var result = await service.RepointSeriesAsync(row.Id, "RIGHTASIN");

            // Then: ASIN pinned, sync went through the by-ASIN path (mock verified by setup match)
            Assert.NotNull(result);
            Assert.False(result!.Merged);
            Assert.Equal("RIGHTASIN", result.MonitoredSeries!.SeriesAsin);
            Assert.True(result.MonitoredSeries.AsinPinned);
            _seriesCatalogService.Verify(service => service.GetCatalogByAsinAsync(
                "Smugglers Tale", "RIGHTASIN", "us", 500, null, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task RepointSeriesAsync_CollapsesIntoExistingMonitorForSameAsin()
        {
            // Given: a healthy row already tracks the target ASIN; repointing a stale row onto it
            // must merge (delete the stale row, pin + return the survivor).
            Init(services => services
                .WithSingleton(_seriesCatalogService.Object)
                .WithSingleton(_libraryAddService.Object));

            var repo = _provider.GetRequiredService<IMonitoredSeriesRepository>();
            var survivor = await repo.UpsertAsync(new MonitoredSeries
            {
                SeriesName = "Smuggler's Tales",
                SeriesNameNormalized = "smuggler s tales",
                SeriesAsin = "RIGHTASIN",
                Region = "us",
                Language = "all"
            });
            var stale = await repo.UpsertAsync(new MonitoredSeries
            {
                SeriesName = "A Smugglers Tale",
                SeriesNameNormalized = "a smugglers tale",
                SeriesAsin = "WRONGASIN",
                Region = "us",
                Language = "all"
            });

            _seriesCatalogService
                .Setup(service => service.GetCatalogByAsinAsync(
                    It.IsAny<string>(),
                    "RIGHTASIN",
                    It.IsAny<string>(),
                    500,
                    null,
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SeriesCatalogFetchResultBuilder()
                    .WithSeries("Smuggler's Tales", "RIGHTASIN")
                    .Build());

            var service = _provider.GetRequiredService<ISeriesMonitoringService>();

            // When
            var result = await service.RepointSeriesAsync(stale.Id, "RIGHTASIN");

            // Then
            Assert.NotNull(result);
            Assert.True(result!.Merged);
            Assert.Equal(survivor.Id, result.MonitoredSeries!.Id);
            Assert.True(result.MonitoredSeries.AsinPinned);
            Assert.Null(await repo.GetByIdAsync(stale.Id));
        }

    }
}
