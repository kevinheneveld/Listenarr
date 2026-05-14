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
using Listenarr.Infrastructure.Models;
using Listenarr.Infrastructure.Repositories;

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

        // ── Overview ─────────────────────────────────────────────────────────

        [Fact]
        public async Task GetLibraryStats_Overview_CountsBooksFilesMonitoredAndDuration()
        {
            using var db = NewDb();
            var withFiles = FullyPopulatedBook("Foundation");
            withFiles.Files = new List<AudiobookFile>
            {
                new() { Path = "/a.m4b", Size = 1000, DurationSeconds = 3600, Codec = "aac", Bitrate = 128000 },
                new() { Path = "/b.m4b", Size = 2000, DurationSeconds = 7200, Codec = "aac", Bitrate = 128000 },
            };
            db.Audiobooks.Add(withFiles);
            db.Audiobooks.Add(EmptyBook("No Files Book"));
            await db.SaveChangesAsync();

            var stats = await new LibraryStatsRepository(db).GetLibraryStatsAsync();

            Assert.Equal(2, stats.Overview.TotalBooks);
            Assert.Equal(1, stats.Overview.MonitoredBooks);
            Assert.Equal(1, stats.Overview.UnmonitoredBooks);
            Assert.Equal(1, stats.Overview.BooksWithFiles);
            Assert.Equal(1, stats.Overview.BooksWithoutFiles);
            Assert.Equal(2, stats.Overview.TotalFiles);
            Assert.Equal(3000, stats.Overview.TotalSizeBytes);
            // File durations (3h total) win over the Runtime estimate.
            Assert.Equal(3.0, stats.Overview.TotalDurationHours);
        }

        [Fact]
        public async Task GetLibraryStats_Duration_FallsBackToRuntimeWhenNoFiles()
        {
            using var db = NewDb();
            // Runtime 600 min = 10 hrs, no files.
            db.Audiobooks.Add(FullyPopulatedBook("Runtime Only"));
            await db.SaveChangesAsync();

            var stats = await new LibraryStatsRepository(db).GetLibraryStatsAsync();

            Assert.Equal(10.0, stats.Overview.TotalDurationHours);
            Assert.Equal("10–20 hrs", Assert.Single(stats.DurationDistribution, d => d.Count == 1).Label);
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
            // Foundation: own 1 of 3 cached → incomplete, missing 2.
            db.Audiobooks.Add(new Audiobook { Title = "Foundation", Series = "Foundation" });
            // Dune: own 2 of 2 cached → complete.
            db.Audiobooks.Add(new Audiobook { Title = "Dune", Series = "Dune" });
            db.Audiobooks.Add(new Audiobook { Title = "Dune Messiah", Series = "Dune" });
            // Mystery: own 1, no cached catalog → unknown.
            db.Audiobooks.Add(new Audiobook { Title = "Mystery One", Series = "Mystery" });
            // Standalone.
            db.Audiobooks.Add(new Audiobook { Title = "Standalone" });

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

            Assert.Equal(3, series.TotalSeries);
            Assert.Equal(1, series.CompleteSeries);
            Assert.Equal(1, series.IncompleteSeries);
            Assert.Equal(1, series.UnknownCompletenessSeries);
            Assert.Equal(4, series.BooksInSeries);
            Assert.Equal(1, series.StandaloneBooks);
            Assert.Equal(2, series.MissingBooksAcrossSeries);
        }

        [Fact]
        public async Task GetLibraryStats_Series_PrefersPrimaryMembershipOverSeriesField()
        {
            using var db = NewDb();
            db.Audiobooks.Add(new Audiobook
            {
                Title = "Crossover",
                Series = "Legacy Field Series",
                SeriesMemberships = new List<AudiobookSeriesMembership>
                {
                    new() { SeriesName = "Primary Series", IsPrimary = true, SortOrder = 0 },
                },
            });
            await db.SaveChangesAsync();

            var series = (await new LibraryStatsRepository(db).GetLibraryStatsAsync()).Series;

            Assert.Equal(1, series.TotalSeries);
            Assert.Equal(1, series.BooksInSeries);
            Assert.Equal(0, series.StandaloneBooks);
        }

        // ── Genres / authors / languages / quality ───────────────────────────

        [Fact]
        public async Task GetLibraryStats_GenresAuthorsLanguages_AreGroupedAndCounted()
        {
            using var db = NewDb();
            db.Audiobooks.Add(new Audiobook
            {
                Title = "A",
                Authors = new List<string> { "Asimov" },
                Genres = new List<string> { "Sci-Fi", "Classic" },
                Language = "english",
            });
            db.Audiobooks.Add(new Audiobook
            {
                Title = "B",
                Authors = new List<string> { "Asimov" },
                Genres = new List<string> { "Sci-Fi" },
                Language = "English",
            });
            db.Audiobooks.Add(new Audiobook { Title = "C" }); // no language → Unknown
            await db.SaveChangesAsync();

            var stats = await new LibraryStatsRepository(db).GetLibraryStatsAsync();

            Assert.Equal(2, stats.TopGenres.First(g => g.Genre == "Sci-Fi").Count);
            Assert.Equal(1, stats.TopGenres.First(g => g.Genre == "Classic").Count);
            Assert.Equal(1, stats.Authors.TotalAuthors);
            Assert.Equal(2, stats.Authors.TopAuthors.Single().Count);
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

            db.DownloadHistories.Add(new DownloadHistory { DownloadId = "1", EventType = DownloadHistoryEventType.Imported });
            db.DownloadHistories.Add(new DownloadHistory { DownloadId = "2", EventType = DownloadHistoryEventType.Imported });
            db.DownloadHistories.Add(new DownloadHistory { DownloadId = "3", EventType = DownloadHistoryEventType.ImportFailed });
            await db.SaveChangesAsync();

            // Default granularity is monthly, 12 periods.
            var activity = (await new LibraryStatsRepository(db).GetLibraryStatsAsync()).Activity;

            Assert.Equal(ActivityGranularity.Month, activity.Granularity);
            Assert.Equal(12, activity.BooksAddedByPeriod.Count);
            Assert.Equal(2, activity.BooksAddedByPeriod.Last().Count); // current month
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
