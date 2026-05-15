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
 */
using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Listenarr.Application.Repositories;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Models;

namespace Listenarr.Infrastructure.Repositories
{
    /// <summary>
    /// Computes library-wide aggregate metrics. The library is loaded into
    /// memory once (no-tracking) and reduced in C# rather than via SQL: many
    /// fields are JSON-encoded list columns (Authors, Genres, Narrators) that
    /// don't translate to SQL aggregates, and a homelab library is small enough
    /// that a single in-memory pass is cheap. If a library ever outgrows this,
    /// the composed <see cref="LibraryStats"/> shape lets individual sections
    /// be optimised independently.
    /// </summary>
    public class LibraryStatsRepository : ILibraryStatsRepository
    {
        private const int TopGenreCount = 15;
        private const int TopAuthorCount = 15;
        private const int TopNarratorCount = 15;
        private const int MinActivityPeriods = 1;
        private const int MaxActivityPeriods = 365;

        private readonly ListenArrDbContext _db;

        public LibraryStatsRepository(ListenArrDbContext db)
        {
            _db = db;
        }

        public async Task<LibraryStats> GetLibraryStatsAsync(
            ActivityGranularity activityGranularity = ActivityGranularity.Month,
            int activityPeriods = 12,
            CancellationToken ct = default)
        {
            var periods = Math.Clamp(activityPeriods, MinActivityPeriods, MaxActivityPeriods);

            var books = await _db.Audiobooks
                .AsNoTracking()
                .Include(a => a.Files)
                .Include(a => a.SeriesMemberships)
                .ToListAsync(ct);

            var seriesCatalog = await _db.SeriesCacheEntries
                .AsNoTracking()
                .Select(s => new { s.SeriesNameNormalized, CatalogCount = s.CatalogBooks!.Count })
                .ToListAsync(ct);

            var addedHistory = await _db.History
                .AsNoTracking()
                .Where(h => h.EventType == "Added")
                .Select(h => h.Timestamp)
                .ToListAsync(ct);

            var importEvents = await _db.DownloadHistories
                .AsNoTracking()
                .Where(d => d.EventType == DownloadHistoryEventType.Imported
                            || d.EventType == DownloadHistoryEventType.ImportFailed)
                .Select(d => new { d.EventType, d.EventDate })
                .ToListAsync(ct);
            var importedTimestamps = importEvents
                .Where(e => e.EventType == DownloadHistoryEventType.Imported)
                .Select(e => e.EventDate)
                .ToList();
            var importEventTypes = importEvents.Select(e => e.EventType).ToList();

            return new LibraryStats
            {
                Overview = BuildOverview(books),
                MetadataCompleteness = BuildMetadataCompleteness(books),
                Series = BuildSeriesStats(books, seriesCatalog
                    .GroupBy(s => s.SeriesNameNormalized)
                    .ToDictionary(g => g.Key, g => g.Max(x => x.CatalogCount))),
                Authors = BuildAuthorStats(books),
                Narrators = BuildNarratorStats(books),
                Quality = BuildQualityStats(books),
                Activity = BuildActivityStats(addedHistory, importedTimestamps, importEventTypes, activityGranularity, periods),
                TopGenres = BuildTopGenres(books),
                DurationDistribution = BuildDurationDistribution(books),
                Languages = BuildLanguages(books),
                GeneratedAt = DateTime.UtcNow,
            };
        }

        // ── Overview ─────────────────────────────────────────────────────────

        // A book is "owned" when its file(s) are present on disk. With no
        // per-book expected-file-count, presence of any file is the proxy for
        // "the user actually has this book".
        private static bool IsOwned(Audiobook book) => book.Files is { Count: > 0 };

        private static LibraryOverviewStats BuildOverview(List<Audiobook> books)
        {
            var owned = books.Where(IsOwned).ToList();
            return new LibraryOverviewStats
            {
                TotalBooks = books.Count,
                OwnedBooks = owned.Count,
                MissingBooks = books.Count - owned.Count,
                MonitoredBooks = books.Count(b => b.Monitored),
                UnmonitoredBooks = books.Count(b => !b.Monitored),
                TotalSizeBytes = books.Sum(b => b.Files?.Sum(f => f.Size ?? 0) ?? 0),
                TotalDurationHours = Math.Round(owned.Sum(EffectiveDurationHours), 1),
                AverageDurationHours = owned.Count == 0
                    ? 0
                    : Math.Round(owned.Sum(EffectiveDurationHours) / owned.Count, 1),
            };
        }

        /// <summary>
        /// A book's duration in hours, preferring summed file durations (verified
        /// from the files on disk) over the metadata Runtime estimate. Returns 0
        /// when neither signal is present.
        /// </summary>
        private static double EffectiveDurationHours(Audiobook book)
        {
            var fileSeconds = book.Files?
                .Where(f => f.DurationSeconds.HasValue && f.DurationSeconds.Value > 0)
                .Sum(f => f.DurationSeconds!.Value) ?? 0;
            if (fileSeconds > 0)
            {
                return fileSeconds / 3600.0;
            }

            return book.Runtime.HasValue && book.Runtime.Value > 0
                ? book.Runtime.Value / 60.0
                : 0;
        }

        private static bool HasAnyDuration(Audiobook book) => EffectiveDurationHours(book) > 0;

        // ── Metadata completeness ────────────────────────────────────────────

        // Always-applicable fields used for the overall completeness score.
        // Series position is reported separately because it only applies to
        // books that belong to a series.
        private static readonly Func<Audiobook, bool>[] CoreFieldPresent =
        {
            b => !string.IsNullOrWhiteSpace(b.ImageUrl),
            b => !string.IsNullOrWhiteSpace(b.Asin),
            b => b.Isbn != null && b.Isbn.Any(i => !string.IsNullOrWhiteSpace(i)),
            b => b.Genres != null && b.Genres.Any(g => !string.IsNullOrWhiteSpace(g)),
            b => b.Narrators != null && b.Narrators.Any(n => !string.IsNullOrWhiteSpace(n)),
            b => !string.IsNullOrWhiteSpace(b.Description),
            b => !string.IsNullOrWhiteSpace(b.Publisher),
            b => !string.IsNullOrWhiteSpace(b.Language),
            b => !string.IsNullOrWhiteSpace(b.PublishedDate) || !string.IsNullOrWhiteSpace(b.PublishYear),
            HasAnyDuration,
        };

        private static MetadataCompletenessStats BuildMetadataCompleteness(List<Audiobook> books)
        {
            var stats = new MetadataCompletenessStats
            {
                TotalBooks = books.Count,
                MissingCoverArt = books.Count(b => string.IsNullOrWhiteSpace(b.ImageUrl)),
                MissingAsin = books.Count(b => string.IsNullOrWhiteSpace(b.Asin)),
                MissingIsbn = books.Count(b => b.Isbn == null || !b.Isbn.Any(i => !string.IsNullOrWhiteSpace(i))),
                MissingGenres = books.Count(b => b.Genres == null || !b.Genres.Any(g => !string.IsNullOrWhiteSpace(g))),
                MissingNarrators = books.Count(b => b.Narrators == null || !b.Narrators.Any(n => !string.IsNullOrWhiteSpace(n))),
                MissingDescription = books.Count(b => string.IsNullOrWhiteSpace(b.Description)),
                MissingPublisher = books.Count(b => string.IsNullOrWhiteSpace(b.Publisher)),
                MissingLanguage = books.Count(b => string.IsNullOrWhiteSpace(b.Language)),
                MissingPublishDate = books.Count(b => string.IsNullOrWhiteSpace(b.PublishedDate) && string.IsNullOrWhiteSpace(b.PublishYear)),
                MissingRuntime = books.Count(b => !HasAnyDuration(b)),
                MissingSeriesPosition = books.Count(b => SeriesNameOf(b) != null && string.IsNullOrWhiteSpace(SeriesNumberOf(b))),
            };

            if (books.Count > 0)
            {
                var totalPresent = books.Sum(b => CoreFieldPresent.Count(present => present(b)));
                stats.OverallCompletenessPercent = Math.Round(
                    100.0 * totalPresent / (books.Count * CoreFieldPresent.Length), 1);
            }

            return stats;
        }

        // ── Series ───────────────────────────────────────────────────────────

        private static string? SeriesNameOf(Audiobook book)
        {
            var primary = book.SeriesMemberships?
                .OrderByDescending(m => m.IsPrimary)
                .ThenBy(m => m.SortOrder)
                .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.SeriesName));
            if (primary != null)
            {
                return primary.SeriesName!.Trim();
            }

            return string.IsNullOrWhiteSpace(book.Series) ? null : book.Series.Trim();
        }

        private static string? SeriesNumberOf(Audiobook book)
        {
            var primary = book.SeriesMemberships?
                .OrderByDescending(m => m.IsPrimary)
                .ThenBy(m => m.SortOrder)
                .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m.SeriesName));
            if (primary != null && !string.IsNullOrWhiteSpace(primary.SeriesNumber))
            {
                return primary.SeriesNumber;
            }

            return book.SeriesNumber;
        }

        private static SeriesStats BuildSeriesStats(
            List<Audiobook> books,
            Dictionary<string, int> catalogCountByNormalizedName)
        {
            // normalized series name -> (books tracked in library, books owned on disk)
            var bySeries = new Dictionary<string, (int Tracked, int Owned)>();
            var standaloneTracked = 0;

            foreach (var book in books)
            {
                var name = SeriesNameOf(book);
                if (name == null)
                {
                    standaloneTracked++;
                    continue;
                }

                var key = NormalizeSeriesName(name);
                var cur = bySeries.GetValueOrDefault(key);
                bySeries[key] = (cur.Tracked + 1, cur.Owned + (IsOwned(book) ? 1 : 0));
            }

            var stats = new SeriesStats();
            var booksInRealSeries = 0;

            foreach (var (normalizedName, counts) in bySeries)
            {
                catalogCountByNormalizedName.TryGetValue(normalizedName, out var catalogCount);

                // Audible labels standalone books as 1-member "series". When neither
                // the library nor the catalog shows 2+ books, it isn't a real series —
                // fold it into the standalone count instead.
                var effectiveSize = Math.Max(counts.Tracked, catalogCount);
                if (effectiveSize <= 1)
                {
                    stats.SingleBookSeriesFolded += counts.Tracked;
                    standaloneTracked += counts.Tracked;
                    continue;
                }

                stats.TotalSeries++;
                booksInRealSeries += counts.Tracked;

                if (catalogCount <= 1)
                {
                    // Real series (2+ owned) but no usable cached catalog to compare against.
                    stats.UnknownCompletenessSeries++;
                }
                else if (counts.Owned >= catalogCount)
                {
                    stats.CompleteSeries++;
                }
                else
                {
                    stats.IncompleteSeries++;
                    // Books I don't yet have a file for, against the known catalog size.
                    stats.MissingBooksAcrossSeries += catalogCount - counts.Owned;
                }
            }

            stats.BooksInSeries = booksInRealSeries;
            stats.StandaloneBooks = standaloneTracked;
            return stats;
        }

        // ── Authors ──────────────────────────────────────────────────────────

        private static AuthorStats BuildAuthorStats(List<Audiobook> books)
        {
            var byAuthor = books
                .Where(b => b.Authors != null)
                .SelectMany(b => b.Authors!
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Select(a => (Author: a.Trim(), Owned: IsOwned(b))))
                .GroupBy(x => x.Author, StringComparer.OrdinalIgnoreCase)
                .Select(g => new AuthorBookCount
                {
                    Author = g.First().Author,
                    TotalBooks = g.Count(),
                    OwnedBooks = g.Count(x => x.Owned),
                })
                .OrderByDescending(a => a.TotalBooks)
                .ThenBy(a => a.Author, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new AuthorStats
            {
                TotalAuthors = byAuthor.Count,
                TopAuthors = byAuthor.Take(TopAuthorCount).ToList(),
            };
        }

        // ── Narrators ────────────────────────────────────────────────────────

        private static NarratorStats BuildNarratorStats(List<Audiobook> books)
        {
            var byNarrator = books
                .Where(b => b.Narrators != null)
                .SelectMany(b => b.Narrators!
                    .Where(n => !string.IsNullOrWhiteSpace(n))
                    .Select(n => (Narrator: n.Trim(), Owned: IsOwned(b))))
                .GroupBy(x => x.Narrator, StringComparer.OrdinalIgnoreCase)
                .Select(g => new NarratorBookCount
                {
                    Narrator = g.First().Narrator,
                    TotalBooks = g.Count(),
                    OwnedBooks = g.Count(x => x.Owned),
                })
                .OrderByDescending(n => n.TotalBooks)
                .ThenBy(n => n.Narrator, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new NarratorStats
            {
                TotalNarrators = byNarrator.Count,
                TopNarrators = byNarrator.Take(TopNarratorCount).ToList(),
            };
        }

        // ── Quality ──────────────────────────────────────────────────────────

        private static QualityStats BuildQualityStats(List<Audiobook> books)
        {
            var files = books
                .Where(b => b.Files != null)
                .SelectMany(b => b.Files!)
                .ToList();

            var byCodec = files
                .GroupBy(f => string.IsNullOrWhiteSpace(f.Codec) ? "Unknown" : f.Codec!.Trim().ToLowerInvariant())
                .Select(g => new CodecCount { Codec = g.Key, Count = g.Count() })
                .OrderByDescending(c => c.Count)
                .ToList();

            var byBitrate = BitrateBuckets
                .Select(bucket => new BitrateBucket
                {
                    Label = bucket.Label,
                    Count = files.Count(f => bucket.Matches(f.Bitrate)),
                })
                .Where(b => b.Count > 0)
                .ToList();

            return new QualityStats { ByCodec = byCodec, ByBitrate = byBitrate };
        }

        // Bitrate is stored in bits/sec; buckets are expressed in kbps.
        private static readonly (string Label, Func<int?, bool> Matches)[] BitrateBuckets =
        {
            ("Unknown", b => !b.HasValue || b.Value <= 0),
            ("< 64 kbps", b => b.HasValue && b.Value > 0 && b.Value / 1000 < 64),
            ("64–128 kbps", b => b.HasValue && b.Value / 1000 >= 64 && b.Value / 1000 < 128),
            ("128–192 kbps", b => b.HasValue && b.Value / 1000 >= 128 && b.Value / 1000 < 192),
            ("192–256 kbps", b => b.HasValue && b.Value / 1000 >= 192 && b.Value / 1000 < 256),
            ("256–320 kbps", b => b.HasValue && b.Value / 1000 >= 256 && b.Value / 1000 < 320),
            ("320+ kbps", b => b.HasValue && b.Value / 1000 >= 320),
        };

        // ── Activity ─────────────────────────────────────────────────────────

        private static ActivityStats BuildActivityStats(
            List<DateTime> addedTimestamps,
            List<DateTime> importedTimestamps,
            List<DownloadHistoryEventType> importEvents,
            ActivityGranularity granularity,
            int periods)
        {
            var addedBuckets = BuildActivityBuckets(addedTimestamps, granularity, periods);
            var importedBuckets = BuildActivityBuckets(importedTimestamps, granularity, periods);

            var imported = importEvents.Count(e => e == DownloadHistoryEventType.Imported);
            var failed = importEvents.Count(e => e == DownloadHistoryEventType.ImportFailed);
            var total = imported + failed;

            return new ActivityStats
            {
                Granularity = granularity,
                BooksAddedByPeriod = addedBuckets,
                BooksImportedByPeriod = importedBuckets,
                TotalImports = imported,
                FailedImports = failed,
                ImportSuccessRate = total == 0 ? 0 : Math.Round(100.0 * imported / total, 1),
            };
        }

        /// <summary>
        /// Produces a zero-filled, oldest-first series of <paramref name="periods"/>
        /// buckets ending with the period containing "now", at the requested
        /// granularity, and counts how many timestamps fall in each.
        /// </summary>
        private static List<ActivityBucket> BuildActivityBuckets(
            List<DateTime> timestamps,
            ActivityGranularity granularity,
            int periods)
        {
            var currentStart = PeriodStart(DateTime.UtcNow, granularity);

            // Bucket starts, oldest first.
            var starts = new List<DateTime>(periods);
            for (var i = periods - 1; i >= 0; i--)
            {
                starts.Add(AddPeriods(currentStart, granularity, -i));
            }

            var buckets = starts
                .Select(start => new ActivityBucket
                {
                    PeriodStart = start,
                    Label = start.ToString(
                        granularity == ActivityGranularity.Month ? "yyyy-MM" : "yyyy-MM-dd",
                        CultureInfo.InvariantCulture),
                    Count = 0,
                })
                .ToList();

            var windowStart = starts[0];
            var windowEnd = AddPeriods(currentStart, granularity, 1);
            foreach (var ts in timestamps)
            {
                var t = ts.Kind == DateTimeKind.Utc ? ts : ts.ToUniversalTime();
                if (t < windowStart || t >= windowEnd)
                {
                    continue;
                }

                var start = PeriodStart(t, granularity);
                var bucket = buckets.FirstOrDefault(b => b.PeriodStart == start);
                if (bucket != null)
                {
                    bucket.Count++;
                }
            }

            return buckets;
        }

        private static DateTime PeriodStart(DateTime utc, ActivityGranularity granularity)
        {
            var date = utc.Date;
            return granularity switch
            {
                ActivityGranularity.Day => DateTime.SpecifyKind(date, DateTimeKind.Utc),
                // Weeks start on Monday.
                ActivityGranularity.Week => DateTime.SpecifyKind(
                    date.AddDays(-((int)date.DayOfWeek + 6) % 7), DateTimeKind.Utc),
                _ => new DateTime(date.Year, date.Month, 1, 0, 0, 0, DateTimeKind.Utc),
            };
        }

        private static DateTime AddPeriods(DateTime start, ActivityGranularity granularity, int count)
        {
            return granularity switch
            {
                ActivityGranularity.Day => start.AddDays(count),
                ActivityGranularity.Week => start.AddDays(7 * count),
                _ => start.AddMonths(count),
            };
        }

        // ── Genres ───────────────────────────────────────────────────────────

        private static List<GenreCount> BuildTopGenres(List<Audiobook> books)
        {
            return books
                .Where(b => b.Genres != null)
                .SelectMany(b => b.Genres!
                    .Where(g => !string.IsNullOrWhiteSpace(g))
                    .Select(g => (Genre: g.Trim(), Owned: IsOwned(b))))
                .GroupBy(x => x.Genre, StringComparer.OrdinalIgnoreCase)
                .Select(g => new GenreCount
                {
                    Genre = g.First().Genre,
                    TotalBooks = g.Count(),
                    OwnedBooks = g.Count(x => x.Owned),
                })
                .OrderByDescending(g => g.TotalBooks)
                .ThenBy(g => g.Genre, StringComparer.OrdinalIgnoreCase)
                .Take(TopGenreCount)
                .ToList();
        }

        // ── Duration distribution ────────────────────────────────────────────

        private static readonly (string Label, Func<double, bool> Matches)[] DurationBuckets =
        {
            ("< 2 hrs", h => h > 0 && h < 2),
            ("2–5 hrs", h => h >= 2 && h < 5),
            ("5–10 hrs", h => h >= 5 && h < 10),
            ("10–20 hrs", h => h >= 10 && h < 20),
            ("20+ hrs", h => h >= 20),
            ("Unknown", h => h <= 0),
        };

        private static List<DurationBucket> BuildDurationDistribution(List<Audiobook> books)
        {
            // Each book contributes (hours, owned-flag) to a single bucket so
            // the bucket can be split into owned vs missing in the dashboard.
            var entries = books.Select(b => (Hours: EffectiveDurationHours(b), Owned: IsOwned(b))).ToList();
            return DurationBuckets
                .Select(bucket =>
                {
                    var inBucket = entries.Where(e => bucket.Matches(e.Hours)).ToList();
                    return new DurationBucket
                    {
                        Label = bucket.Label,
                        TotalBooks = inBucket.Count,
                        OwnedBooks = inBucket.Count(e => e.Owned),
                    };
                })
                .ToList();
        }

        // ── Languages ────────────────────────────────────────────────────────

        private static List<LanguageCount> BuildLanguages(List<Audiobook> books)
        {
            return books
                .GroupBy(b => string.IsNullOrWhiteSpace(b.Language)
                    ? "Unknown"
                    : CultureInfo.InvariantCulture.TextInfo.ToTitleCase(b.Language.Trim().ToLowerInvariant()))
                .Select(g => new LanguageCount { Language = g.Key, Count = g.Count() })
                .OrderByDescending(l => l.Count)
                .ThenBy(l => l.Language, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        /// <summary>
        /// Mirrors SeriesMonitoringService.NormalizeSeriesName so that owned
        /// books match cached series catalog entries on the same key.
        /// </summary>
        private static string NormalizeSeriesName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return string.Empty;
            }

            var decomposed = name.Trim().Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(decomposed.Length);
            foreach (var character in decomposed)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                {
                    continue;
                }

                if (char.IsLetterOrDigit(character))
                {
                    builder.Append(char.ToLowerInvariant(character));
                }
                else if (char.IsWhiteSpace(character))
                {
                    builder.Append(' ');
                }
            }

            return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }
    }
}
