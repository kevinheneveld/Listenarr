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
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Domain.Models;
using Listenarr.Infrastructure.Persistence;

namespace Listenarr.Infrastructure.Persistence.Repositories
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
        private const int ActivityMonths = 12;

        private readonly ListenArrDbContext _db;

        public LibraryStatsRepository(ListenArrDbContext db)
        {
            _db = db;
        }

        public async Task<LibraryStats> GetLibraryStatsAsync(CancellationToken ct = default)
        {
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
                .Select(d => d.EventType)
                .ToListAsync(ct);

            return new LibraryStats
            {
                Overview = BuildOverview(books),
                MetadataCompleteness = BuildMetadataCompleteness(books),
                Series = BuildSeriesStats(books, seriesCatalog
                    .GroupBy(s => s.SeriesNameNormalized)
                    .ToDictionary(g => g.Key, g => g.Max(x => x.CatalogCount))),
                Authors = BuildAuthorStats(books),
                Quality = BuildQualityStats(books),
                Activity = BuildActivityStats(addedHistory, importEvents),
                TopGenres = BuildTopGenres(books),
                DurationDistribution = BuildDurationDistribution(books),
                Languages = BuildLanguages(books),
                GeneratedAt = DateTime.UtcNow,
            };
        }

        // ── Overview ─────────────────────────────────────────────────────────

        private static LibraryOverviewStats BuildOverview(List<Audiobook> books)
        {
            var withFiles = books.Where(b => b.Files != null && b.Files.Count > 0).ToList();
            return new LibraryOverviewStats
            {
                TotalBooks = books.Count,
                MonitoredBooks = books.Count(b => b.Monitored),
                UnmonitoredBooks = books.Count(b => !b.Monitored),
                BooksWithFiles = withFiles.Count,
                BooksWithoutFiles = books.Count - withFiles.Count,
                TotalFiles = books.Sum(b => b.Files?.Count ?? 0),
                TotalSizeBytes = books.Sum(b => b.Files?.Sum(f => f.Size ?? 0) ?? 0),
                TotalDurationHours = Math.Round(books.Sum(EffectiveDurationHours), 1),
                AverageDurationHours = books.Count == 0
                    ? 0
                    : Math.Round(books.Sum(EffectiveDurationHours) / books.Count, 1),
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
            var booksBySeries = new Dictionary<string, int>();
            var standalone = 0;

            foreach (var book in books)
            {
                var name = SeriesNameOf(book);
                if (name == null)
                {
                    standalone++;
                    continue;
                }

                var key = NormalizeSeriesName(name);
                booksBySeries[key] = booksBySeries.GetValueOrDefault(key) + 1;
            }

            var stats = new SeriesStats
            {
                TotalSeries = booksBySeries.Count,
                StandaloneBooks = standalone,
                BooksInSeries = booksBySeries.Values.Sum(),
            };

            foreach (var (normalizedName, ownedCount) in booksBySeries)
            {
                if (!catalogCountByNormalizedName.TryGetValue(normalizedName, out var catalogCount)
                    || catalogCount <= 0)
                {
                    stats.UnknownCompletenessSeries++;
                    continue;
                }

                if (ownedCount >= catalogCount)
                {
                    stats.CompleteSeries++;
                }
                else
                {
                    stats.IncompleteSeries++;
                    stats.MissingBooksAcrossSeries += catalogCount - ownedCount;
                }
            }

            return stats;
        }

        // ── Authors ──────────────────────────────────────────────────────────

        private static AuthorStats BuildAuthorStats(List<Audiobook> books)
        {
            var byAuthor = books
                .Where(b => b.Authors != null)
                .SelectMany(b => b.Authors!)
                .Where(a => !string.IsNullOrWhiteSpace(a))
                .Select(a => a.Trim())
                .GroupBy(a => a, StringComparer.OrdinalIgnoreCase)
                .Select(g => new AuthorBookCount { Author = g.First(), Count = g.Count() })
                .OrderByDescending(a => a.Count)
                .ThenBy(a => a.Author, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new AuthorStats
            {
                TotalAuthors = byAuthor.Count,
                TopAuthors = byAuthor.Take(TopAuthorCount).ToList(),
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
            List<DownloadHistoryEventType> importEvents)
        {
            var now = DateTime.UtcNow;
            var months = new List<MonthlyCount>();
            for (var i = ActivityMonths - 1; i >= 0; i--)
            {
                var month = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(-i);
                var label = month.ToString("yyyy-MM", CultureInfo.InvariantCulture);
                var count = addedTimestamps.Count(t =>
                    t.Year == month.Year && t.Month == month.Month);
                months.Add(new MonthlyCount { Month = label, Count = count });
            }

            var imported = importEvents.Count(e => e == DownloadHistoryEventType.Imported);
            var failed = importEvents.Count(e => e == DownloadHistoryEventType.ImportFailed);
            var total = imported + failed;

            return new ActivityStats
            {
                BooksAddedByMonth = months,
                TotalImports = imported,
                FailedImports = failed,
                ImportSuccessRate = total == 0 ? 0 : Math.Round(100.0 * imported / total, 1),
            };
        }

        // ── Genres ───────────────────────────────────────────────────────────

        private static List<GenreCount> BuildTopGenres(List<Audiobook> books)
        {
            return books
                .Where(b => b.Genres != null)
                .SelectMany(b => b.Genres!)
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Select(g => g.Trim())
                .GroupBy(g => g, StringComparer.OrdinalIgnoreCase)
                .Select(g => new GenreCount { Genre = g.First(), Count = g.Count() })
                .OrderByDescending(g => g.Count)
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
            var hours = books.Select(EffectiveDurationHours).ToList();
            return DurationBuckets
                .Select(bucket => new DurationBucket
                {
                    Label = bucket.Label,
                    Count = hours.Count(bucket.Matches),
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
