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
using Microsoft.EntityFrameworkCore;
using Xunit;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Persistence;
using Listenarr.Infrastructure.Persistence.Repositories;

namespace Listenarr.Tests.Features.Infrastructure.Repositories
{
    public class LibraryStatsRepositoryTests
    {
        private static ListenArrDbContext NewDb() => new(
            new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseInMemoryDatabase(Guid.NewGuid().ToString())
                .Options);

        private static Audiobook FullyPopulatedBook(string title) => new()
        {
            Title = title,
            Authors = new List<string> { "Isaac Asimov" },
            ImageUrl = "https://example/cover.jpg",
            Asin = "B00ABC1234",
            Isbn = new List<string> { "9780000000001" },
            Genres = new List<string> { "Science Fiction" },
            Narrators = new List<string> { "Scott Brick" },
            Description = "A description.",
            Publisher = "Listenarr Press",
            Language = "English",
            PublishedDate = "1951-06-01",
            Runtime = 600,
            Monitored = true,
        };

        private static Audiobook EmptyBook(string title) => new()
        {
            Title = title,
            Monitored = false,
        };

        // A book the user actually owns — has a file on disk.
        private static Audiobook OwnedBook(
            string title,
            string? series = null,
            double durationSeconds = 3600,
            long size = 1000) => new()
        {
            Title = title,
            Series = series,
            Files = new List<AudiobookFile>
            {
                new() { Path = $"/{title}.m4b", Size = size, DurationSeconds = durationSeconds },
            },
        };

        // ── Overview ─────────────────────────────────────────────────────────

        [Fact]
        public async Task GetLibraryStats_Overview_CountsOwnedVsMissingMonitoredAndDuration()
        {
            using var db = NewDb();
            var owned = FullyPopulatedBook("Foundation");
            owned.Files = new List<AudiobookFile>
            {
                new() { Path = "/a.m4b", Size = 1000, DurationSeconds = 3600 },
                new() { Path = "/b.m4b", Size = 2000, DurationSeconds = 7200 },
            };
            db.Audiobooks.Add(owned);
            db.Audiobooks.Add(EmptyBook("No Files Book")); // tracked but not owned
            await db.SaveChangesAsync();

            var stats = await new LibraryStatsRepository(db).GetLibraryStatsAsync();

            Assert.Equal(2, stats.Overview.TotalBooks);
            Assert.Equal(1, stats.Overview.OwnedBooks);
            Assert.Equal(1, stats.Overview.MissingBooks);
            Assert.Equal(1, stats.Overview.MonitoredBooks);
            Assert.Equal(1, stats.Overview.UnmonitoredBooks);
            Assert.Equal(3000, stats.Overview.TotalSizeBytes);
            // Duration counts owned books only; file durations (3h) beat the Runtime estimate.
            Assert.Equal(3.0, stats.Overview.TotalDurationHours);
            Assert.Equal(3.0, stats.Overview.AverageDurationHours);
        }

        [Fact]
        public async Task GetLibraryStats_Overview_ExcludesMissingBooksFromDuration()
        {
            using var db = NewDb();
            // Runtime 600 min = 10 hrs as metadata, but no file → not owned.
            db.Audiobooks.Add(FullyPopulatedBook("Runtime Only"));
            await db.SaveChangesAsync();

            var stats = await new LibraryStatsRepository(db).GetLibraryStatsAsync();

            // Not owned, so it contributes nothing to the owned-duration total...
            Assert.Equal(0.0, stats.Overview.TotalDurationHours);
            // ...but the duration distribution still buckets it via the Runtime fallback,
            // and the bucket's OwnedBooks split correctly stays 0.
            var bucket = Assert.Single(stats.DurationDistribution, d => d.TotalBooks == 1);
            Assert.Equal("10–20 hrs", bucket.Label);
            Assert.Equal(0, bucket.OwnedBooks);
        }

        // ── Metadata completeness ────────────────────────────────────────────

        [Fact]
        public async Task GetLibraryStats_MetadataCompleteness_CountsMissingFieldsAndScore()
        {
            using var db = NewDb();
            db.Audiobooks.Add(FullyPopulatedBook("Complete"));
            db.Audiobooks.Add(EmptyBook("Empty"));
            await db.SaveChangesAsync();

            var mc = (await new LibraryStatsRepository(db).GetLibraryStatsAsync()).MetadataCompleteness;

            Assert.Equal(2, mc.TotalBooks);
            // Exactly one book (the empty one) is missing each core field.
            Assert.Equal(1, mc.MissingCoverArt);
            Assert.Equal(1, mc.MissingAsin);
            Assert.Equal(1, mc.MissingIsbn);
            Assert.Equal(1, mc.MissingGenres);
            Assert.Equal(1, mc.MissingNarrators);
            Assert.Equal(1, mc.MissingDescription);
            Assert.Equal(1, mc.MissingPublisher);
            Assert.Equal(1, mc.MissingLanguage);
            Assert.Equal(1, mc.MissingPublishDate);
            Assert.Equal(1, mc.MissingRuntime);
            // One book fully populated (10/10), one empty (0/10) → 50%.
            Assert.Equal(50.0, mc.OverallCompletenessPercent);
        }

        [Fact]
        public async Task GetLibraryStats_MissingSeriesPosition_OnlyCountsBooksInASeries()
        {
            using var db = NewDb();
            // In a series, no position → counts.
            db.Audiobooks.Add(new Audiobook { Title = "In Series", Series = "Foundation" });
            // Standalone, no position → does NOT count.
            db.Audiobooks.Add(new Audiobook { Title = "Standalone" });
            await db.SaveChangesAsync();

            var mc = (await new LibraryStatsRepository(db).GetLibraryStatsAsync()).MetadataCompleteness;

            Assert.Equal(1, mc.MissingSeriesPosition);
        }

        // ── Series completeness ──────────────────────────────────────────────

        [Fact]
        public async Task GetLibraryStats_Series_ClassifiesCompleteIncompleteAndUnknown()
        {
            using var db = NewDb();
            // Foundation: own a file for 1, 1 more tracked-but-missing, catalog of 3 → incomplete, missing 2.
            db.Audiobooks.Add(OwnedBook("Foundation", series: "Foundation"));
            db.Audiobooks.Add(new Audiobook { Title = "Foundation and Empire", Series = "Foundation" });
            // Dune: own files for both, catalog of 2 → complete.
            db.Audiobooks.Add(OwnedBook("Dune", series: "Dune"));
            db.Audiobooks.Add(OwnedBook("Dune Messiah", series: "Dune"));
            // Wheel of Time: own 2, no cached catalog → real series, completeness unknown.
            db.Audiobooks.Add(OwnedBook("The Eye of the World", series: "Wheel of Time"));
            db.Audiobooks.Add(OwnedBook("The Great Hunt", series: "Wheel of Time"));
            // Mystery: a single-book Audible "series", no catalog → folded into standalone.
            db.Audiobooks.Add(OwnedBook("Mystery One", series: "Mystery"));
            // A genuine standalone.
            db.Audiobooks.Add(OwnedBook("Standalone"));

            db.SeriesCacheEntries.Add(new SeriesCacheEntry
            {
                SeriesName = "Foundation",
                SeriesNameNormalized = "foundation",
                CatalogBooks = new List<CachedSeriesCatalogBook>
                {
                    new() { Title = "Foundation" },
                    new() { Title = "Foundation and Empire" },
                    new() { Title = "Second Foundation" },
                },
            });
            db.SeriesCacheEntries.Add(new SeriesCacheEntry
            {
                SeriesName = "Dune",
                SeriesNameNormalized = "dune",
                CatalogBooks = new List<CachedSeriesCatalogBook>
                {
                    new() { Title = "Dune" },
                    new() { Title = "Dune Messiah" },
                },
            });
            await db.SaveChangesAsync();

            var series = (await new LibraryStatsRepository(db).GetLibraryStatsAsync()).Series;

            Assert.Equal(3, series.TotalSeries); // Foundation, Dune, Wheel of Time
            Assert.Equal(1, series.CompleteSeries); // Dune
            Assert.Equal(1, series.IncompleteSeries); // Foundation
            Assert.Equal(1, series.UnknownCompletenessSeries); // Wheel of Time
            Assert.Equal(6, series.BooksInSeries); // 2 + 2 + 2 tracked across real series
            Assert.Equal(2, series.StandaloneBooks); // Mystery (folded) + Standalone
            Assert.Equal(1, series.SingleBookSeriesFolded); // Mystery
            Assert.Equal(2, series.MissingBooksAcrossSeries); // Foundation: 3 catalog - 1 owned
        }

        [Fact]
        public async Task GetLibraryStats_Series_FoldsSingleBookSeriesIntoStandalone()
        {
            using var db = NewDb();
            // Single-book "series", no catalog → folded.
            db.Audiobooks.Add(OwnedBook("Solo", series: "Solo"));
            // Single tracked book but a 1-entry catalog → still effectively size 1 → folded.
            db.Audiobooks.Add(OwnedBook("Singleton", series: "Singleton"));
            db.SeriesCacheEntries.Add(new SeriesCacheEntry
            {
                SeriesName = "Singleton",
                SeriesNameNormalized = "singleton",
                CatalogBooks = new List<CachedSeriesCatalogBook> { new() { Title = "Singleton" } },
            });
            // Two tracked books → a real series even with no catalog.
            db.Audiobooks.Add(OwnedBook("Pair One", series: "Pair"));
            db.Audiobooks.Add(OwnedBook("Pair Two", series: "Pair"));
            // One tracked book, but the catalog says the series has 5 → a real (incomplete) series.
            db.Audiobooks.Add(OwnedBook("Known Vol 1", series: "Known"));
            db.SeriesCacheEntries.Add(new SeriesCacheEntry
            {
                SeriesName = "Known",
                SeriesNameNormalized = "known",
                CatalogBooks = Enumerable.Range(1, 5)
                    .Select(i => new CachedSeriesCatalogBook { Title = $"Known Vol {i}" })
                    .ToList(),
            });
            await db.SaveChangesAsync();

            var series = (await new LibraryStatsRepository(db).GetLibraryStatsAsync()).Series;

            Assert.Equal(2, series.TotalSeries); // Pair, Known
            Assert.Equal(2, series.SingleBookSeriesFolded); // Solo, Singleton
            Assert.Equal(2, series.StandaloneBooks); // Solo + Singleton, folded
            Assert.Equal(3, series.BooksInSeries); // Pair (2) + Known (1)
            Assert.Equal(1, series.IncompleteSeries); // Known: own 1 of 5
            Assert.Equal(4, series.MissingBooksAcrossSeries); // Known: 5 - 1
        }

        [Fact]
        public async Task GetLibraryStats_Series_PrefersPrimaryMembershipOverSeriesField()
        {
            using var db = NewDb();
            // Two books, both whose primary membership says "Primary Series" while the
            // legacy Series field says something else — they should group as one real series.
            foreach (var title in new[] { "Crossover One", "Crossover Two" })
            {
                db.Audiobooks.Add(new Audiobook
                {
                    Title = title,
                    Series = "Legacy Field Series",
                    Files = new List<AudiobookFile> { new() { Path = $"/{title}.m4b" } },
                    SeriesMemberships = new List<AudiobookSeriesMembership>
                    {
                        new() { SeriesName = "Primary Series", IsPrimary = true, SortOrder = 0 },
                    },
                });
            }
            await db.SaveChangesAsync();

            var series = (await new LibraryStatsRepository(db).GetLibraryStatsAsync()).Series;

            Assert.Equal(1, series.TotalSeries);
            Assert.Equal(2, series.BooksInSeries);
            Assert.Equal(0, series.StandaloneBooks);
        }

        // ── Genres / authors / languages / quality ───────────────────────────

        [Fact]
        public async Task GetLibraryStats_GenresAuthorsNarratorsLanguages_AreGroupedAndCounted()
        {
            using var db = NewDb();
            // Book A is owned (has a file); book B is tracked but missing.
            db.Audiobooks.Add(new Audiobook
            {
                Title = "A",
                Authors = new List<string> { "Asimov" },
                Narrators = new List<string> { "Scott Brick", "Grover Gardner" },
                Genres = new List<string> { "Sci-Fi", "Classic" },
                Language = "english",
                Files = new List<AudiobookFile> { new() { Path = "/a.m4b" } },
            });
            db.Audiobooks.Add(new Audiobook
            {
                Title = "B",
                Authors = new List<string> { "Asimov" },
                Narrators = new List<string> { "scott brick" },
                Genres = new List<string> { "Sci-Fi" },
                Language = "English",
            });
            db.Audiobooks.Add(new Audiobook { Title = "C" }); // no language → Unknown
            await db.SaveChangesAsync();

            var stats = await new LibraryStatsRepository(db).GetLibraryStatsAsync();

            // Genres carry the same have/missing split as authors and narrators.
            var sciFi = stats.TopGenres.First(g => g.Genre == "Sci-Fi");
            Assert.Equal(2, sciFi.TotalBooks);
            Assert.Equal(1, sciFi.OwnedBooks);
            var classic = stats.TopGenres.First(g => g.Genre == "Classic");
            Assert.Equal(1, classic.TotalBooks);
            Assert.Equal(1, classic.OwnedBooks);

            // Authors: total tracked vs. actually owned.
            Assert.Equal(1, stats.Authors.TotalAuthors);
            var asimov = stats.Authors.TopAuthors.Single();
            Assert.Equal(2, asimov.TotalBooks);
            Assert.Equal(1, asimov.OwnedBooks);

            // Narrators are grouped case-insensitively, same as authors, with the same split.
            Assert.Equal(2, stats.Narrators.TotalNarrators);
            var scottBrick = stats.Narrators.TopNarrators.First(n => n.Narrator == "Scott Brick");
            Assert.Equal(2, scottBrick.TotalBooks);
            Assert.Equal(1, scottBrick.OwnedBooks);
            var groverGardner = stats.Narrators.TopNarrators.First(n => n.Narrator == "Grover Gardner");
            Assert.Equal(1, groverGardner.TotalBooks);
            Assert.Equal(1, groverGardner.OwnedBooks);

            // "english"/"English" collapse to one bucket; missing → "Unknown".
            Assert.Equal(2, stats.Languages.First(l => l.Language == "English").Count);
            Assert.Equal(1, stats.Languages.First(l => l.Language == "Unknown").Count);
        }

        [Fact]
        public async Task GetLibraryStats_Quality_BucketsCodecsAndBitrates()
        {
            using var db = NewDb();
            var book = new Audiobook
            {
                Title = "Quality Book",
                Files = new List<AudiobookFile>
                {
                    new() { Path = "/a", Codec = "aac", Bitrate = 64000 },   // 64 kbps
                    new() { Path = "/b", Codec = "mp3", Bitrate = 320000 },  // 320 kbps
                    new() { Path = "/c", Codec = null, Bitrate = null },     // Unknown / Unknown
                },
            };
            db.Audiobooks.Add(book);
            await db.SaveChangesAsync();

            var quality = (await new LibraryStatsRepository(db).GetLibraryStatsAsync()).Quality;

            Assert.Equal(1, quality.ByCodec.First(c => c.Codec == "aac").Count);
            Assert.Equal(1, quality.ByCodec.First(c => c.Codec == "mp3").Count);
            Assert.Equal(1, quality.ByCodec.First(c => c.Codec == "Unknown").Count);
            Assert.Equal(1, quality.ByBitrate.First(b => b.Label == "64–128 kbps").Count);
            Assert.Equal(1, quality.ByBitrate.First(b => b.Label == "320+ kbps").Count);
            Assert.Equal(1, quality.ByBitrate.First(b => b.Label == "Unknown").Count);
        }

        // ── Activity ─────────────────────────────────────────────────────────

        [Fact]
        public async Task GetLibraryStats_Activity_CountsAddedByMonthAndImportSuccessRate()
        {
            using var db = NewDb();
            db.Audiobooks.Add(new Audiobook { Title = "A" });

            var now = DateTime.UtcNow;
            db.History.Add(new History { EventType = "Added", Timestamp = now });
            db.History.Add(new History { EventType = "Added", Timestamp = now });
            db.History.Add(new History { EventType = "Updated", Timestamp = now }); // ignored

            db.DownloadHistories.Add(new DownloadHistory { DownloadId = "1", EventType = DownloadHistoryEventType.Imported, EventDate = now });
            db.DownloadHistories.Add(new DownloadHistory { DownloadId = "2", EventType = DownloadHistoryEventType.Imported, EventDate = now });
            db.DownloadHistories.Add(new DownloadHistory { DownloadId = "3", EventType = DownloadHistoryEventType.ImportFailed, EventDate = now });
            await db.SaveChangesAsync();

            // Default granularity is monthly, 12 periods.
            var activity = (await new LibraryStatsRepository(db).GetLibraryStatsAsync()).Activity;

            Assert.Equal(ActivityGranularity.Month, activity.Granularity);
            Assert.Equal(12, activity.BooksAddedByPeriod.Count);
            Assert.Equal(2, activity.BooksAddedByPeriod.Last().Count); // current month
            // Imported events are bucketed in their own series alongside Added.
            Assert.Equal(12, activity.BooksImportedByPeriod.Count);
            Assert.Equal(2, activity.BooksImportedByPeriod.Last().Count); // current month
            Assert.Equal(2, activity.TotalImports);
            Assert.Equal(1, activity.FailedImports);
            Assert.Equal(66.7, activity.ImportSuccessRate);
        }

        [Fact]
        public async Task GetLibraryStats_Activity_SupportsDailyGranularityAndWindowSize()
        {
            using var db = NewDb();
            var now = DateTime.UtcNow;
            db.History.Add(new History { EventType = "Added", Timestamp = now });
            db.History.Add(new History { EventType = "Added", Timestamp = now.AddDays(-3) });
            db.History.Add(new History { EventType = "Added", Timestamp = now.AddDays(-40) }); // outside 30-day window
            await db.SaveChangesAsync();

            var activity = (await new LibraryStatsRepository(db)
                .GetLibraryStatsAsync(ActivityGranularity.Day, 30)).Activity;

            Assert.Equal(ActivityGranularity.Day, activity.Granularity);
            Assert.Equal(30, activity.BooksAddedByPeriod.Count);
            Assert.Equal(1, activity.BooksAddedByPeriod.Last().Count);                 // today
            Assert.Equal(1, activity.BooksAddedByPeriod[^4].Count);                    // 3 days ago
            Assert.Equal(2, activity.BooksAddedByPeriod.Sum(b => b.Count));            // 40-days-ago excluded
        }

        [Fact]
        public async Task GetLibraryStats_Activity_SupportsWeeklyGranularity()
        {
            using var db = NewDb();
            var now = DateTime.UtcNow;
            db.History.Add(new History { EventType = "Added", Timestamp = now });
            db.History.Add(new History { EventType = "Added", Timestamp = now.AddDays(-8) }); // a previous week
            await db.SaveChangesAsync();

            var activity = (await new LibraryStatsRepository(db)
                .GetLibraryStatsAsync(ActivityGranularity.Week, 8)).Activity;

            Assert.Equal(ActivityGranularity.Week, activity.Granularity);
            Assert.Equal(8, activity.BooksAddedByPeriod.Count);
            Assert.Equal(2, activity.BooksAddedByPeriod.Sum(b => b.Count));
            // Weeks start on Monday — bucket starts are 7 days apart and ascending.
            for (var i = 1; i < activity.BooksAddedByPeriod.Count; i++)
            {
                Assert.Equal(
                    activity.BooksAddedByPeriod[i - 1].PeriodStart.AddDays(7),
                    activity.BooksAddedByPeriod[i].PeriodStart);
            }
        }

        [Fact]
        public async Task GetLibraryStats_Activity_ClampsAbsurdPeriodCounts()
        {
            using var db = NewDb();
            var activity = (await new LibraryStatsRepository(db)
                .GetLibraryStatsAsync(ActivityGranularity.Day, 100_000)).Activity;

            Assert.Equal(365, activity.BooksAddedByPeriod.Count);
        }

        // ── Empty library ────────────────────────────────────────────────────

        [Fact]
        public async Task GetLibraryStats_EmptyLibrary_ReturnsZeroedStats()
        {
            using var db = NewDb();
            var stats = await new LibraryStatsRepository(db).GetLibraryStatsAsync();

            Assert.Equal(0, stats.Overview.TotalBooks);
            Assert.Equal(0, stats.MetadataCompleteness.OverallCompletenessPercent);
            Assert.Equal(0, stats.Series.TotalSeries);
            Assert.Equal(0, stats.Activity.ImportSuccessRate);
            Assert.Empty(stats.TopGenres);
            Assert.Equal(12, stats.Activity.BooksAddedByPeriod.Count);
        }
    }
}
