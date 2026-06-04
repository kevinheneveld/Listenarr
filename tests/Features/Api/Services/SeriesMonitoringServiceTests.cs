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
using Listenarr.Application.Audiobooks;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Metadata;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Persistence;
using Listenarr.Infrastructure.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Api.Services
{
    public class SeriesMonitoringServiceTests
    {
        [Fact]
        public async Task MonitorSeriesAsync_PersistsSeriesAndAddsOnlyMissingBooksForSelectedLanguage()
        {
            var dbOptions = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(databaseName: $"series-monitor-{Guid.NewGuid():N}")
                .Options;

            await using var dbContext = new ListenArrDbContext(dbOptions);
            dbContext.Audiobooks.Add(new Audiobook
            {
                Id = 10,
                Title = "The Final Empire",
                Authors = new List<string> { "Brandon Sanderson" },
                Series = "Mistborn",
                Language = "english",
                Monitored = true
            });
            await dbContext.SaveChangesAsync();

            var seriesCatalogService = new Mock<ISeriesCatalogService>();
            seriesCatalogService
                .Setup(service => service.GetCatalogAsync("Mistborn", "uk", 500, null, true, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SeriesCatalogFetchResult
                {
                    Series = new SeriesLookupItem
                    {
                        Asin = "SERIES123",
                        Name = "Mistborn"
                    },
                    Books = new List<AudibleSearchResult>
                    {
                        new()
                        {
                            Title = "The Final Empire",
                            Authors = new List<AudibleAuthor> { new() { Name = "Brandon Sanderson" } },
                            Language = "en-us",
                            Series = new List<AudibleSeries> { new() { Name = "Mistborn", Position = "1" } }
                        },
                        new()
                        {
                            Asin = "BOOK2",
                            Title = "The Well of Ascension",
                            Authors = new List<AudibleAuthor> { new() { Name = "Brandon Sanderson" } },
                            Language = "english",
                            Series = new List<AudibleSeries> { new() { Name = "Mistborn", Position = "2" } }
                        },
                        new()
                        {
                            Asin = "BOOK3",
                            Title = "Held der Zeiten",
                            Authors = new List<AudibleAuthor> { new() { Name = "Brandon Sanderson" } },
                            Language = "de",
                            Series = new List<AudibleSeries> { new() { Name = "Mistborn", Position = "3" } }
                        }
                    }
                });

            var libraryAddService = new Mock<ILibraryAddService>();
            libraryAddService
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
                    Audiobook = new Audiobook
                    {
                        Id = 11,
                        Title = "The Well of Ascension",
                        Authors = new List<string> { "Brandon Sanderson" },
                        Series = "Mistborn",
                        Asin = "BOOK2",
                        Monitored = true
                    }
                });

            var seriesRepo = new EfMonitoredSeriesRepository(dbContext);
            var audiobooksRepo = new AudiobookRepository(dbContext);

            var service = new SeriesMonitoringService(
                seriesRepo,
                audiobooksRepo,
                seriesCatalogService.Object,
                libraryAddService.Object,
                Mock.Of<ILogger<SeriesMonitoringService>>());

            var result = await service.MonitorSeriesAsync(new MonitorSeriesRequest
            {
                Name = "Mistborn",
                Region = "uk",
                Language = "english"
            });

            Assert.NotNull(result.MonitoredSeries);
            Assert.True(result.SyncResult.Succeeded);
            Assert.Equal(1, result.SyncResult.AddedCount);
            Assert.Equal(1, result.SyncResult.ExistingCount);
            Assert.Equal(0, result.SyncResult.FailedCount);
            Assert.Equal("SERIES123", result.MonitoredSeries!.SeriesAsin);
            Assert.Equal("uk", result.MonitoredSeries.Region);
            Assert.Equal("english", result.MonitoredSeries.Language);
            Assert.NotNull(result.MonitoredSeries.LastSuccessfulSyncAt);

            libraryAddService.Verify(service => service.AddToLibraryAsync(
                    It.IsAny<LibraryAddOperationRequest>(),
                    It.IsAny<CancellationToken>()),
                Times.Once);

            var storedSeries = await dbContext.MonitoredSeries.SingleAsync();
            Assert.Equal("Mistborn", storedSeries.SeriesName);
            Assert.Equal("mistborn", storedSeries.SeriesNameNormalized);
            Assert.Equal("SERIES123", storedSeries.SeriesAsin);
        }

        [Fact]
        public async Task RepointSeriesAsync_PinsAsinAndResolvesByAsinAndSurvivesResync()
        {
            var dbOptions = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(databaseName: $"series-repoint-{Guid.NewGuid():N}")
                .Options;

            await using var dbContext = new ListenArrDbContext(dbOptions);

            // A book the user already owns, so the resolved catalog matches it (no add needed).
            dbContext.Audiobooks.Add(new Audiobook
            {
                Id = 20,
                Title = "Milk Run",
                Authors = new List<string> { "Nathan Lowell" },
                Series = "Smuggler's Tales",
                Asin = "OWNED1",
                Language = "english",
                Monitored = true
            });

            // An existing monitored series whose name once resolved to the WRONG (empty) series.
            dbContext.MonitoredSeries.Add(new MonitoredSeries
            {
                Id = 5,
                SeriesName = "A Smugglers Tale",
                SeriesNameNormalized = "a smugglers tale",
                SeriesAsin = null,
                AsinPinned = false,
                Region = "us",
                Language = "all"
            });
            await dbContext.SaveChangesAsync();

            var seriesCatalogService = new Mock<ISeriesCatalogService>();
            // The pinned-ASIN path returns the correct catalog. Its Series.Asin deliberately
            // differs from the pinned value to prove the pinned ASIN is NOT clobbered on sync.
            seriesCatalogService
                .Setup(service => service.GetCatalogByAsinAsync(
                    "A Smugglers Tale", "PINNED123", "us", 500, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SeriesCatalogFetchResult
                {
                    Series = new SeriesLookupItem { Asin = "RESOLVED_OTHER", Name = "Smuggler's Tales" },
                    Books = new List<AudibleSearchResult>
                    {
                        new()
                        {
                            Asin = "OWNED1",
                            Title = "Milk Run",
                            Authors = new List<AudibleAuthor> { new() { Name = "Nathan Lowell" } },
                            Language = "english",
                            Series = new List<AudibleSeries> { new() { Name = "Smuggler's Tales", Position = "1" } }
                        }
                    }
                });

            var libraryAddService = new Mock<ILibraryAddService>();
            var seriesRepo = new EfMonitoredSeriesRepository(dbContext);
            var audiobooksRepo = new AudiobookRepository(dbContext);

            var service = new SeriesMonitoringService(
                seriesRepo,
                audiobooksRepo,
                seriesCatalogService.Object,
                libraryAddService.Object,
                Mock.Of<ILogger<SeriesMonitoringService>>());

            // Pin the correct ASIN onto the mis-resolved series.
            var repoint = await service.RepointSeriesAsync(5, "PINNED123");

            Assert.NotNull(repoint);
            Assert.NotNull(repoint!.MonitoredSeries);
            Assert.True(repoint.SyncResult.Succeeded);
            Assert.True(repoint.MonitoredSeries!.AsinPinned);
            Assert.Equal("PINNED123", repoint.MonitoredSeries.SeriesAsin);

            // Re-sync (e.g. the daily background sweep) must keep resolving by the pinned ASIN
            // and must not let name-resolution overwrite the pinned value.
            var resync = await service.SyncSeriesAsync(5);
            Assert.True(resync.Succeeded);

            var storedSeries = await dbContext.MonitoredSeries.SingleAsync();
            Assert.True(storedSeries.AsinPinned);
            Assert.Equal("PINNED123", storedSeries.SeriesAsin);

            // The by-ASIN path was used for both the repoint and the re-sync; the name path never.
            seriesCatalogService.Verify(service => service.GetCatalogByAsinAsync(
                "A Smugglers Tale", "PINNED123", "us", 500, null, It.IsAny<CancellationToken>()),
                Times.Exactly(2));
            seriesCatalogService.Verify(service => service.GetCatalogAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<string>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()),
                Times.Never);

            // The owned book matched, so nothing was added.
            libraryAddService.Verify(service => service.AddToLibraryAsync(
                It.IsAny<LibraryAddOperationRequest>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task RepointSeriesAsync_WhenAsinAlreadyMonitored_MergesIntoSurvivorAndDeletesSource()
        {
            var dbOptions = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(databaseName: $"series-merge-{Guid.NewGuid():N}")
                .Options;

            await using var dbContext = new ListenArrDbContext(dbOptions);

            dbContext.Audiobooks.Add(new Audiobook
            {
                Id = 30,
                Title = "Milk Run",
                Authors = new List<string> { "Nathan Lowell" },
                Series = "Smuggler's Tales",
                Asin = "OWNED1",
                Language = "english",
                Monitored = true
            });

            // The stale orphan the user is repointing from.
            dbContext.MonitoredSeries.Add(new MonitoredSeries
            {
                Id = 5,
                SeriesName = "A Smugglers Tale",
                SeriesNameNormalized = "a smugglers tale",
                SeriesAsin = null,
                AsinPinned = false,
                Region = "us",
                Language = "all"
            });
            // A healthy entry already monitoring the target ASIN.
            dbContext.MonitoredSeries.Add(new MonitoredSeries
            {
                Id = 6,
                SeriesName = "Smuggler's Tales",
                SeriesNameNormalized = "smuggler's tales",
                SeriesAsin = "PINNED123",
                AsinPinned = false,
                Region = "us",
                Language = "all"
            });
            await dbContext.SaveChangesAsync();

            var seriesCatalogService = new Mock<ISeriesCatalogService>();
            seriesCatalogService
                .Setup(service => service.GetCatalogByAsinAsync(
                    "Smuggler's Tales", "PINNED123", "us", 500, null, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new SeriesCatalogFetchResult
                {
                    Series = new SeriesLookupItem { Asin = "PINNED123", Name = "Smuggler's Tales" },
                    Books = new List<AudibleSearchResult>
                    {
                        new()
                        {
                            Asin = "OWNED1",
                            Title = "Milk Run",
                            Authors = new List<AudibleAuthor> { new() { Name = "Nathan Lowell" } },
                            Language = "english",
                            Series = new List<AudibleSeries> { new() { Name = "Smuggler's Tales", Position = "1" } }
                        }
                    }
                });

            var service = new SeriesMonitoringService(
                new EfMonitoredSeriesRepository(dbContext),
                new AudiobookRepository(dbContext),
                seriesCatalogService.Object,
                Mock.Of<ILibraryAddService>(),
                Mock.Of<ILogger<SeriesMonitoringService>>());

            // Repoint the orphan (id 5) at an ASIN that id 6 already monitors.
            var result = await service.RepointSeriesAsync(5, "PINNED123");

            Assert.NotNull(result);
            Assert.True(result!.Merged);
            Assert.True(result.SyncResult.Succeeded);
            // The survivor (id 6) is returned, pinned; the source row is gone.
            Assert.Equal(6, result.MonitoredSeries!.Id);
            Assert.True(result.MonitoredSeries.AsinPinned);
            Assert.Equal("PINNED123", result.MonitoredSeries.SeriesAsin);

            var remaining = await dbContext.MonitoredSeries.ToListAsync();
            Assert.Single(remaining);
            Assert.Equal(6, remaining[0].Id);
            Assert.True(remaining[0].AsinPinned);
        }
    }
}
