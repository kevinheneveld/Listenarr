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
using System;
using Listenarr.Application.Audiobooks;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Application.Metadata;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Persistence;
using Listenarr.Infrastructure.Persistence.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Api.Services
{
    public class SeriesCatalogServiceTests
    {
        [Fact]
        public async Task GetCatalogAsync_UsesPersistedCatalogCache_BeforeAudible()
        {
            using var httpClientForAudible = new HttpClient();
            var audible = new Mock<AudibleService>(httpClientForAudible, Mock.Of<ILogger<AudibleService>>()) { CallBase = false };
            var audiobookRepository = new Mock<IAudiobookRepository>();
            var logger = new Mock<ILogger<SeriesCatalogService>>();

            audiobookRepository
                .Setup(repository => repository.GetCachedSeriesByNameAsync("Mistborn", "us"))
                .ReturnsAsync(new SeriesCacheEntry
                {
                    SeriesName = "Mistborn",
                    SeriesNameNormalized = "mistborn",
                    SeriesAsin = "SERIES123",
                    Region = "us",
                    ImageUrl = "mistborn.jpg",
                    Description = "Persisted series description",
                    CatalogBooks = new List<CachedSeriesCatalogBook>
                    {
                        new()
                        {
                            Asin = "BOOK1",
                            Title = "The Final Empire",
                            Authors = new List<string> { "Brandon Sanderson" },
                            Language = "english"
                        },
                        new()
                        {
                            Asin = "BOOK2",
                            Title = "The Well of Ascension",
                            Authors = new List<string> { "Brandon Sanderson" },
                            Language = "german"
                        }
                    }
                });

            var service = new SeriesCatalogService(
                audible.Object,
                audiobookRepository.Object,
                logger.Object);

            var result = await service.GetCatalogAsync("Mistborn", "us", 10, "english");

            Assert.NotNull(result);
            Assert.Single(result!.Books);
            Assert.Equal("The Final Empire", result.Books[0].Title);
            Assert.Equal("Mistborn", result.Series.Name);

            audible.Verify(service => service.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            audible.Verify(service => service.GetTypedBooksBySeriesAsinAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task GetCatalogAsync_ForceRefresh_BypassesPersistedCatalogCache_AndPersistsFreshBooks()
        {
            using var httpClientForAudible = new HttpClient();
            var audible = new Mock<AudibleService>(httpClientForAudible, Mock.Of<ILogger<AudibleService>>()) { CallBase = false };
            var audiobookRepository = new Mock<IAudiobookRepository>();
            var logger = new Mock<ILogger<SeriesCatalogService>>();

            audiobookRepository
                .Setup(repository => repository.GetCachedSeriesByNameAsync("Mistborn", "us"))
                .ReturnsAsync(new SeriesCacheEntry
                {
                    SeriesName = "Mistborn",
                    SeriesNameNormalized = "mistborn",
                    SeriesAsin = "SERIES123",
                    Region = "us",
                    ImageUrl = "old-mistborn.jpg",
                    CatalogBooks = new List<CachedSeriesCatalogBook>
                    {
                        new()
                        {
                            Title = "Old Cached Book",
                            Authors = new List<string> { "Brandon Sanderson" },
                            MetadataSource = "OpenLibrary"
                        }
                    }
                });

            audiobookRepository
                .Setup(repository => repository.UpsertCachedSeriesAsync(It.IsAny<SeriesCacheEntry>()))
                .ReturnsAsync((SeriesCacheEntry entry) => entry);

            audible
                .Setup(service => service.GetTypedBooksBySeriesAsinAsync("SERIES123", "us"))
                .ReturnsAsync(new List<AudibleSearchResult>
                {
                    new()
                    {
                        Asin = "BOOK1",
                        Title = "The Final Empire",
                        Authors = new List<AudibleAuthor> { new() { Name = "Brandon Sanderson" } },
                        ImageUrl = "final-empire.jpg",
                        Language = "english",
                        Link = "https://audible.example/final-empire",
                        Series = new List<AudibleSeries> { new() { Name = "Mistborn", Position = "1" } }
                    }
                });

            var service = new SeriesCatalogService(
                audible.Object,
                audiobookRepository.Object,
                logger.Object);

            var result = await service.GetCatalogAsync("Mistborn", "us", 10, forceRefresh: true);

            Assert.NotNull(result);
            Assert.Single(result!.Books);
            Assert.Equal("The Final Empire", result.Books[0].Title);

            audible.Verify(
                svc => svc.GetTypedBooksBySeriesAsinAsync("SERIES123", "us"),
                Times.Once);
            audiobookRepository.Verify(
                repository => repository.UpsertCachedSeriesAsync(It.Is<SeriesCacheEntry>(entry =>
                    entry.SeriesAsin == "SERIES123" &&
                    entry.CatalogBooks != null &&
                    entry.CatalogBooks.Count == 1 &&
                    entry.CatalogBooks[0].Title == "The Final Empire")),
                Times.Once);
        }

        [Fact]
        public async Task GetCatalogAsync_RecoversSeriesFromOwnedBook_AndPrefersItOverWrongNameSearch()
        {
            using var httpClientForAudible = new HttpClient();
            var audible = new Mock<AudibleService>(httpClientForAudible, Mock.Of<ILogger<AudibleService>>()) { CallBase = false };
            var audiobookRepository = new Mock<IAudiobookRepository>();
            var logger = new Mock<ILogger<SeriesCatalogService>>();

            // Cache miss.
            audiobookRepository
                .Setup(repository => repository.GetCachedSeriesByNameAsync("Seeker's Tale", "us"))
                .ReturnsAsync((SeriesCacheEntry?)null);

            // The user owns a book in this series.
            audiobookRepository
                .Setup(repository => repository.GetLibraryAsync())
                .ReturnsAsync(new List<Audiobook>
                {
                    new()
                    {
                        Id = 1,
                        Title = "In Ashes Born",
                        Series = "Seeker's Tale",
                        Asin = "BOOK1",
                        Authors = new List<string> { "Nathan Lowell" }
                    }
                });

            audiobookRepository
                .Setup(repository => repository.UpsertCachedSeriesAsync(It.IsAny<SeriesCacheEntry>()))
                .ReturnsAsync((SeriesCacheEntry entry) => entry);

            // A plain name search would resolve a confidently-wrong series.
            audible
                .Setup(service => service.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<string>()))
                .ReturnsAsync(new SeriesLookupItem { Asin = "WRONG_SERIES", Name = "Seekers" });

            // The owned book resolves to the authoritative series ASIN via its own metadata.
            audible
                .Setup(service => service.GetBookMetadataAsync("BOOK1", "us", It.IsAny<bool>(), It.IsAny<string?>()))
                .ReturnsAsync(new AudibleBookResponse
                {
                    Asin = "BOOK1",
                    Title = "In Ashes Born",
                    Series = new List<AudibleSeries> { new() { Asin = "CORRECT_SERIES", Name = "Seeker's Tale", Position = "1" } }
                });

            audible
                .Setup(service => service.GetSeriesByAsinAsync("CORRECT_SERIES", "us"))
                .ReturnsAsync(new SeriesLookupItem { Asin = "CORRECT_SERIES", Name = "Seeker's Tale" });

            audible
                .Setup(service => service.GetTypedBooksBySeriesAsinAsync("CORRECT_SERIES", "us"))
                .ReturnsAsync(new List<AudibleSearchResult>
                {
                    new() { Asin = "BOOK1", Title = "In Ashes Born", Language = "english" }
                });

            var service = new SeriesCatalogService(audible.Object, audiobookRepository.Object, logger.Object);

            var result = await service.GetCatalogAsync("Seeker's Tale", "us", 10);

            Assert.NotNull(result);
            Assert.Equal("CORRECT_SERIES", result!.Series.Asin);
            Assert.Equal("Seeker's Tale", result.Series.Name);

            // Owned-book recovery is preferred; the wrong name search is never relied on.
            audible.Verify(service => service.LookupSeriesAsync(It.IsAny<string>(), It.IsAny<string>()), Times.Never);
            audible.Verify(service => service.GetTypedBooksBySeriesAsinAsync("CORRECT_SERIES", "us"), Times.Once);

            // The recovered ASIN is persisted so subsequent loads resolve by ASIN.
            audiobookRepository.Verify(
                repository => repository.UpsertCachedSeriesAsync(It.Is<SeriesCacheEntry>(entry =>
                    entry.SeriesAsin == "CORRECT_SERIES")),
                Times.Once);
        }

        [Fact]
        public async Task GetCatalogAsync_FallsBackToNameSearch_WhenNoOwnedBookMatches()
        {
            using var httpClientForAudible = new HttpClient();
            var audible = new Mock<AudibleService>(httpClientForAudible, Mock.Of<ILogger<AudibleService>>()) { CallBase = false };
            var audiobookRepository = new Mock<IAudiobookRepository>();
            var logger = new Mock<ILogger<SeriesCatalogService>>();

            audiobookRepository
                .Setup(repository => repository.GetCachedSeriesByNameAsync("Wheel of Time", "us"))
                .ReturnsAsync((SeriesCacheEntry?)null);

            // Library has no book in this series.
            audiobookRepository
                .Setup(repository => repository.GetLibraryAsync())
                .ReturnsAsync(new List<Audiobook>());

            audiobookRepository
                .Setup(repository => repository.UpsertCachedSeriesAsync(It.IsAny<SeriesCacheEntry>()))
                .ReturnsAsync((SeriesCacheEntry entry) => entry);

            audible
                .Setup(service => service.LookupSeriesAsync("Wheel of Time", "us"))
                .ReturnsAsync(new SeriesLookupItem { Asin = "WOT", Name = "Wheel of Time" });

            audible
                .Setup(service => service.GetTypedBooksBySeriesAsinAsync("WOT", "us"))
                .ReturnsAsync(new List<AudibleSearchResult>
                {
                    new() { Asin = "B", Title = "The Eye of the World", Language = "english" }
                });

            var service = new SeriesCatalogService(audible.Object, audiobookRepository.Object, logger.Object);

            var result = await service.GetCatalogAsync("Wheel of Time", "us", 10);

            Assert.NotNull(result);
            Assert.Equal("WOT", result!.Series.Asin);

            // No owned book, so no book metadata is probed; name search is used.
            audible.Verify(
                service => service.GetBookMetadataAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(), It.IsAny<string?>()),
                Times.Never);
            audible.Verify(service => service.LookupSeriesAsync("Wheel of Time", "us"), Times.Once);
        }

        [Fact]
        public async Task GetCatalogAsync_OwnedBookInMultipleSeries_ResolvesOnlyTheMatchingSeries()
        {
            using var httpClientForAudible = new HttpClient();
            var audible = new Mock<AudibleService>(httpClientForAudible, Mock.Of<ILogger<AudibleService>>()) { CallBase = false };
            var audiobookRepository = new Mock<IAudiobookRepository>();
            var logger = new Mock<ILogger<SeriesCatalogService>>();

            audiobookRepository
                .Setup(repository => repository.GetCachedSeriesByNameAsync("Seeker's Tale", "us"))
                .ReturnsAsync((SeriesCacheEntry?)null);

            audiobookRepository
                .Setup(repository => repository.GetLibraryAsync())
                .ReturnsAsync(new List<Audiobook>
                {
                    new() { Id = 1, Title = "In Ashes Born", Series = "Seeker's Tale", Asin = "BOOK1" }
                });

            audiobookRepository
                .Setup(repository => repository.UpsertCachedSeriesAsync(It.IsAny<SeriesCacheEntry>()))
                .ReturnsAsync((SeriesCacheEntry entry) => entry);

            // The book belongs to two series; only the one whose name matches must be used.
            audible
                .Setup(service => service.GetBookMetadataAsync("BOOK1", "us", It.IsAny<bool>(), It.IsAny<string?>()))
                .ReturnsAsync(new AudibleBookResponse
                {
                    Asin = "BOOK1",
                    Title = "In Ashes Born",
                    Series = new List<AudibleSeries>
                    {
                        new() { Asin = "GOLDEN_AGE", Name = "Golden Age of the Solar Clipper" },
                        new() { Asin = "CORRECT_SERIES", Name = "Seeker's Tale" }
                    }
                });

            audible
                .Setup(service => service.GetSeriesByAsinAsync("CORRECT_SERIES", "us"))
                .ReturnsAsync(new SeriesLookupItem { Asin = "CORRECT_SERIES", Name = "Seeker's Tale" });

            audible
                .Setup(service => service.GetTypedBooksBySeriesAsinAsync("CORRECT_SERIES", "us"))
                .ReturnsAsync(new List<AudibleSearchResult>
                {
                    new() { Asin = "BOOK1", Title = "In Ashes Born", Language = "english" }
                });

            var service = new SeriesCatalogService(audible.Object, audiobookRepository.Object, logger.Object);

            var result = await service.GetCatalogAsync("Seeker's Tale", "us", 10);

            Assert.NotNull(result);
            Assert.Equal("CORRECT_SERIES", result!.Series.Asin);
            audible.Verify(service => service.GetSeriesByAsinAsync("GOLDEN_AGE", It.IsAny<string>()), Times.Never);
        }

        [Fact]
        public async Task SelectThenReload_PicksStick_AndCatalogReadServesChosenSeries()
        {
            // Real repository + real SQLite (not InMemory) so the CatalogBooks JSON value
            // converter is exercised on read-back exactly as in production.
            using var connection = new SqliteConnection("DataSource=:memory:");
            connection.Open();
            using var db = new ListenArrDbContext(
                new DbContextOptionsBuilder<ListenArrDbContext>()
                    .UseSqlite(connection)
                    .Options);
            db.Database.EnsureCreated();
            var repository = new AudiobookRepository(db);

            using var httpClientForAudible = new HttpClient();
            var audible = new Mock<AudibleService>(httpClientForAudible, Mock.Of<ILogger<AudibleService>>()) { CallBase = false };
            var logger = new Mock<ILogger<SeriesCatalogService>>();

            audible
                .Setup(service => service.GetSeriesByAsinAsync("B015EXHCEE", "us"))
                .ReturnsAsync(new SeriesLookupItem { Asin = "B015EXHCEE", Name = "A John Matherson Novel" });
            audible
                .Setup(service => service.GetTypedBooksBySeriesAsinAsync("B015EXHCEE", "us"))
                .ReturnsAsync(new List<AudibleSearchResult>
                {
                    new() { Asin = "B002V1O7UU", Title = "One Second After", Language = "english" },
                    new() { Asin = "B00OYR1L2K", Title = "One Year After", Language = "english" }
                });

            var service = new SeriesCatalogService(audible.Object, repository, logger.Object);

            // Seed the poisoned resolution the way the resolve path would: slug "After" -> wrong series.
            await repository.UpsertCachedSeriesForSlugAsync("After", new SeriesCacheEntry
            {
                SeriesName = "After",
                SeriesAsin = "B07NYRLYKW",
                Region = "us",
                CatalogBooks = new List<CachedSeriesCatalogBook> { new() { Asin = "X", Title = "Wrong Book" } }
            });

            // User picks the correct series.
            var selected = await service.GetCatalogByAsinAsync("After", "B015EXHCEE", "us");
            Assert.NotNull(selected);
            Assert.Equal("B015EXHCEE", selected!.Series.Asin);

            // Direct cache read must see the chosen series persisted under the slug.
            var direct = await repository.GetCachedSeriesByNameAsync("After", "us");
            Assert.NotNull(direct);
            Assert.Equal("B015EXHCEE", direct!.SeriesAsin);
            Assert.NotNull(direct.CatalogBooks);
            Assert.True(direct.CatalogBooks!.Count > 0, "CatalogBooks should round-trip non-empty");

            // Reload via the normal catalog read path: must now serve the chosen series from cache,
            // WITHOUT re-resolving (no Audible series-by-asin call beyond the select).
            audible.Invocations.Clear();
            var reloaded = await service.GetCatalogAsync("After", "us");

            Assert.NotNull(reloaded);
            Assert.Equal("B015EXHCEE", reloaded!.Series.Asin);
            Assert.Equal("A John Matherson Novel", reloaded.Series.Name);
            // The poisoned entry must be gone, not duplicated.
            Assert.Equal(1, await db.SeriesCacheEntries.CountAsync(e => e.SeriesNameNormalized == "after"));
            var afterRow = await db.SeriesCacheEntries.FirstAsync(e => e.SeriesNameNormalized == "after");
            Assert.Equal("B015EXHCEE", afterRow.SeriesAsin);
        }

        [Fact]
        public async Task GetSeriesCandidatesAsync_RanksOwnedBookSeriesAboveWrongNameSearch()
        {
            using var httpClientForAudible = new HttpClient();
            var audible = new Mock<AudibleService>(httpClientForAudible, Mock.Of<ILogger<AudibleService>>()) { CallBase = false };
            var audiobookRepository = new Mock<IAudiobookRepository>();
            var logger = new Mock<ILogger<SeriesCatalogService>>();

            // Owned book stored under a mis-parsed series name ("After") with no ASIN.
            audiobookRepository
                .Setup(repository => repository.GetLibraryAsync())
                .ReturnsAsync(new List<Audiobook>
                {
                    new()
                    {
                        Id = 1,
                        Title = "One Year After",
                        Series = "After",
                        Asin = null,
                        Authors = new List<string> { "William R. Forstchen" }
                    }
                });

            // No ASIN -> title+author lookup returns the book's real series.
            audible
                .Setup(service => service.SearchByTitleAndAuthorAsync(
                    "One Year After", "William R. Forstchen", 1, 5, "us", It.IsAny<string?>()))
                .ReturnsAsync(new AudibleSearchResponse
                {
                    Results = new List<AudibleSearchResult>
                    {
                        new()
                        {
                            Asin = "B0FORST001",
                            Title = "One Year After",
                            Series = new List<AudibleSeries> { new() { Asin = "B015EXHCEE", Name = "A John Matherson Novel", Position = "2" } }
                        }
                    }
                });

            // A plain name search resolves the unrelated romance series.
            audible
                .Setup(service => service.SearchSeriesByNameAsync("After", "us"))
                .ReturnsAsync(new List<SeriesLookupItem>
                {
                    new() { Asin = "B07NYRLYKW", Name = "After", Image = "after.jpg" }
                });

            var service = new SeriesCatalogService(audible.Object, audiobookRepository.Object, logger.Object);

            var result = await service.GetSeriesCandidatesAsync("After", "us");

            // Owned-book-derived series wins the best guess despite its name not matching the slug.
            Assert.Equal("B015EXHCEE", result.BestGuessAsin);
            Assert.Equal(2, result.Candidates.Count);
            Assert.Equal("B015EXHCEE", result.Candidates[0].Asin);
            Assert.Equal("library", result.Candidates[0].Source);
            Assert.Equal(1, result.Candidates[0].OwnedMatchCount);
            Assert.Contains(result.Candidates, candidate => candidate.Asin == "B07NYRLYKW" && candidate.Source == "audible");
        }

        [Fact]
        public async Task GetCatalogByAsinAsync_ResolvesChosenAsin_AndPersistsKeyedBySlug()
        {
            using var httpClientForAudible = new HttpClient();
            var audible = new Mock<AudibleService>(httpClientForAudible, Mock.Of<ILogger<AudibleService>>()) { CallBase = false };
            var audiobookRepository = new Mock<IAudiobookRepository>();
            var logger = new Mock<ILogger<SeriesCatalogService>>();

            audible
                .Setup(service => service.GetSeriesByAsinAsync("B015EXHCEE", "us"))
                .ReturnsAsync(new SeriesLookupItem { Asin = "B015EXHCEE", Name = "A John Matherson Novel", Image = "jm.jpg" });

            audible
                .Setup(service => service.GetTypedBooksBySeriesAsinAsync("B015EXHCEE", "us"))
                .ReturnsAsync(new List<AudibleSearchResult>
                {
                    new() { Asin = "B002V1O7UU", Title = "One Second After", Language = "english" },
                    new() { Asin = "B00OYR1L2K", Title = "One Year After", Language = "english" }
                });

            SeriesCacheEntry? persisted = null;
            string? persistedSlug = null;
            audiobookRepository
                .Setup(repository => repository.UpsertCachedSeriesForSlugAsync(It.IsAny<string>(), It.IsAny<SeriesCacheEntry>()))
                .Callback<string, SeriesCacheEntry>((slug, entry) => { persistedSlug = slug; persisted = entry; })
                .ReturnsAsync((string slug, SeriesCacheEntry entry) => entry);

            var service = new SeriesCatalogService(audible.Object, audiobookRepository.Object, logger.Object);

            // User reached the page via the wrong slug "After" but picks the John Matherson series.
            var result = await service.GetCatalogByAsinAsync("After", "B015EXHCEE", "us", 250);

            Assert.NotNull(result);
            Assert.Equal("B015EXHCEE", result!.Series.Asin);
            Assert.Equal(2, result.Books.Count);

            // Persisted against the slug "After" (so the page reads it back), with the chosen ASIN.
            Assert.Equal("After", persistedSlug);
            Assert.NotNull(persisted);
            Assert.Equal("B015EXHCEE", persisted!.SeriesAsin);
            Assert.Equal("A John Matherson Novel", persisted.SeriesName);
        }
    }
}
