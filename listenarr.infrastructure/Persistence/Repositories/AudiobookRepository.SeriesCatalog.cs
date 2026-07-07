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
using Listenarr.Application.Audiobooks.Series;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Repositories
{
    public partial class AudiobookRepository
    {
        public async Task<Dictionary<string, SeriesCatalogSummary>> GetSeriesCatalogSummariesAsync(
            IReadOnlyCollection<string> seriesNames,
            string region,
            CancellationToken ct = default)
        {
            var result = new Dictionary<string, SeriesCatalogSummary>(StringComparer.OrdinalIgnoreCase);
            if (seriesNames.Count == 0)
            {
                return result;
            }

            var normalizedRegion = AudiobookIdentifierNormalizer.NormalizeRegion(region) ?? "us";

            // Map each caller name to its normalized form; callers never see the
            // normalization rules (they live here, next to the code that wrote
            // the cache rows).
            var normalizedToOriginal = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var name in seriesNames)
            {
                var normalized = NormalizeSeriesName(name);
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    continue;
                }

                // First occurrence wins — duplicate raw spellings of one series
                // resolve to the same cache row anyway.
                normalizedToOriginal.TryAdd(normalized, name);
            }

            if (normalizedToOriginal.Count == 0)
            {
                return result;
            }

            var keys = normalizedToOriginal.Keys.ToList();
            var entries = await _db.SeriesCacheEntries
                .AsNoTracking()
                .Where(e => e.Region == normalizedRegion && keys.Contains(e.SeriesNameNormalized))
                // COALESCE form — see GetCachedSeriesByNameAsync: freshest row wins.
                .OrderByDescending(e => e.LastFetchedAt ?? e.UpdatedAt)
                .ToListAsync(ct);

            foreach (var entry in entries)
            {
                var books = entry.CatalogBooks;
                if (books is not { Count: > 0 })
                {
                    continue;
                }

                if (!normalizedToOriginal.TryGetValue(entry.SeriesNameNormalized, out var original)
                    || result.ContainsKey(original))
                {
                    continue;
                }

                // WORKS, not raw entries: the catalog lists every recording of
                // every book (narrations, dramatizations, re-releases) — the
                // user thinks in books, and gap math must too.
                var workKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var book in books)
                {
                    var key = SeriesWorkKey.Build(book.Title, book.Authors, book.SeriesNumber);
                    if (key.Length > 0)
                    {
                        workKeys.Add(key);
                    }
                }

                var runs = RecordingEditionClassifier.GroupIntoRuns(
                    books,
                    b => RecordingEditionClassifier.Classify(b.Title, b.Subtitle, b.Narrators, b.Publisher, b.PublishedDate));

                if (workKeys.Count > 0)
                {
                    result[original] = new SeriesCatalogSummary(workKeys.Count, runs.Count);
                }
            }

            return result;
        }

        public async Task<List<CachedSeriesCatalogBook>> GetSeriesCatalogEntriesAsync(
            string seriesName,
            string region,
            CancellationToken ct = default)
        {
            var normalized = NormalizeSeriesName(seriesName);
            if (string.IsNullOrWhiteSpace(normalized))
            {
                return new List<CachedSeriesCatalogBook>();
            }

            var normalizedRegion = AudiobookIdentifierNormalizer.NormalizeRegion(region) ?? "us";
            var entry = await _db.SeriesCacheEntries
                .AsNoTracking()
                .Where(e => e.Region == normalizedRegion && e.SeriesNameNormalized == normalized)
                .OrderByDescending(e => e.LastFetchedAt ?? e.UpdatedAt)
                .FirstOrDefaultAsync(ct);

            return entry?.CatalogBooks ?? new List<CachedSeriesCatalogBook>();
        }
    }
}
