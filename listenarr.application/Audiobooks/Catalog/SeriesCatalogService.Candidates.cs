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

using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Catalog
{
    // Series disambiguation: candidate discovery for the "Wrong series?" picker
    // and by-ASIN resolution that makes an explicit user choice stick.
    public partial class SeriesCatalogService
    {
        public async Task<SeriesCandidateResult> GetSeriesCandidatesAsync(
            string name,
            string region = "us",
            CancellationToken cancellationToken = default)
        {
            var result = new SeriesCandidateResult { Query = name?.Trim() ?? string.Empty };
            if (string.IsNullOrWhiteSpace(name))
            {
                return result;
            }

            var normalizedRegion = NormalizeRegion(region);
            var targetKey = NormalizeSeriesCacheKey(name);
            var byAsin = new Dictionary<string, SeriesCandidate>(StringComparer.OrdinalIgnoreCase);

            // Library-sourced candidates: the real Audible series of books the user owns in this
            // collection — surfaced even when their series name doesn't match the slug, which is
            // exactly the case a plain name search gets wrong.
            try
            {
                var library = await _audiobookRepository.GetLibraryAsync() ?? new List<Audiobook>();
                var ownedBooks = library
                    .Where(book => NormalizeSeriesCacheKey(book.Series) == targetKey)
                    .OrderByDescending(book => !string.IsNullOrWhiteSpace(book.Asin))
                    .Take(MaxOwnedBookResolutionAttempts)
                    .ToList();

                foreach (var book in ownedBooks)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (var series in await GetBookSeriesCandidatesAsync(book, normalizedRegion))
                    {
                        var asin = series.Asin!;
                        if (byAsin.TryGetValue(asin, out var existing))
                        {
                            existing.OwnedMatchCount += 1;
                            existing.Source = "library";
                        }
                        else
                        {
                            var candidate = new SeriesCandidate
                            {
                                Asin = asin,
                                Name = series.Name,
                                Source = "library",
                                OwnedMatchCount = 1
                            };
                            byAsin[asin] = candidate;
                            result.Candidates.Add(candidate);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to derive owned-book series candidates for {Series}", name);
            }

            // Name-search candidates: what a plain Audible series lookup returns for the slug.
            try
            {
                if (await _audibleService.SearchSeriesByNameAsync(name, normalizedRegion) is IEnumerable<SeriesLookupItem> items)
                {
                    foreach (var item in items)
                    {
                        if (string.IsNullOrWhiteSpace(item?.Asin) || byAsin.ContainsKey(item.Asin))
                        {
                            continue;
                        }

                        var candidate = new SeriesCandidate
                        {
                            Asin = item.Asin,
                            Name = item.Name,
                            Image = item.Image,
                            Source = "audible"
                        };
                        byAsin[item.Asin] = candidate;
                        result.Candidates.Add(candidate);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to fetch name-search series candidates for {Series}", name);
            }

            // Library-derived candidates first (most-owned first), then name-search results.
            result.Candidates = result.Candidates
                .OrderByDescending(candidate => candidate.Source == "library")
                .ThenByDescending(candidate => candidate.OwnedMatchCount)
                .ToList();
            result.BestGuessAsin = result.Candidates.FirstOrDefault()?.Asin;

            return result;
        }

        public async Task<SeriesCatalogFetchResult?> GetCatalogByAsinAsync(
            string name,
            string asin,
            string region = "us",
            int limit = 250,
            string? language = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(asin))
            {
                return null;
            }

            var normalizedRegion = NormalizeRegion(region);
            var normalizedLanguage = NormalizeLanguage(language);

            var series = await _audibleService.GetSeriesByAsinAsync(asin.Trim(), normalizedRegion);
            if (series == null || string.IsNullOrWhiteSpace(series.Asin))
            {
                return null;
            }

            var books = await _audibleService.GetTypedBooksBySeriesAsinAsync(series.Asin, normalizedRegion)
                ?? new List<AudibleSearchResult>();

            var limitedBooks = books
                .Where(book => book != null)
                .DistinctBy(BuildSeriesCatalogBookKey)
                .Take(Math.Clamp(limit, 1, 500))
                .ToList();

            foreach (var book in limitedBooks)
            {
                book.Series = PrioritizeCatalogSeries(book.Series, series.Asin, series.Name);
            }

            if (string.IsNullOrWhiteSpace(series.Image))
            {
                series.Image = limitedBooks.FirstOrDefault(book => !string.IsNullOrWhiteSpace(book.ImageUrl))?.ImageUrl;
            }

            // Persist under the ORIGINAL name's cache slot so the explicit choice
            // overwrites any prior (possibly wrong) resolution for that name and
            // sticks on later loads (backfill + series health read the same slot).
            var cachedEntry = await ResolvePersistedCacheAsync(name.Trim(), normalizedRegion);
            await PersistCatalogAsync(cachedEntry, name.Trim(), normalizedRegion, series, limitedBooks, cancellationToken);

            return new SeriesCatalogFetchResult
            {
                Series = series,
                Books = FilterCatalogByLanguage(limitedBooks, normalizedLanguage)
            };
        }

        private async Task<List<AudibleSeries>> GetBookSeriesCandidatesAsync(Audiobook book, string region)
        {
            if (!string.IsNullOrWhiteSpace(book.Asin))
            {
                var metadata = await _audibleService.GetBookMetadataAsync(book.Asin, region);
                var fromAsin = FilterUsableSeries(metadata?.Series);
                if (fromAsin.Count > 0)
                {
                    return fromAsin;
                }
            }

            var author = book.Authors?.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            if (!string.IsNullOrWhiteSpace(book.Title) && !string.IsNullOrWhiteSpace(author))
            {
                var response = await _audibleService.SearchByTitleAndAuthorAsync(book.Title!, author!, 1, 5, region);
                return FilterUsableSeries(response?.Results?.FirstOrDefault()?.Series);
            }

            return new List<AudibleSeries>();
        }

        private static List<AudibleSeries> FilterUsableSeries(IEnumerable<AudibleSeries>? series)
        {
            return series?
                .Where(entry => entry != null && !string.IsNullOrWhiteSpace(entry.Asin))
                .ToList() ?? new List<AudibleSeries>();
        }
    }
}
