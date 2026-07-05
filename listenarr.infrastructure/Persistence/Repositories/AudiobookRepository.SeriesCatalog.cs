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

namespace Listenarr.Infrastructure.Persistence.Repositories
{
    public partial class AudiobookRepository
    {
        public async Task<Dictionary<string, int>> GetSeriesCatalogTotalsAsync(
            IReadOnlyCollection<string> seriesNames,
            string region,
            CancellationToken ct = default)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
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
                var total = entry.CatalogBooks?.Count ?? 0;
                if (total <= 0)
                {
                    continue;
                }

                if (normalizedToOriginal.TryGetValue(entry.SeriesNameNormalized, out var original)
                    && !result.ContainsKey(original))
                {
                    result[original] = total;
                }
            }

            return result;
        }
    }
}
