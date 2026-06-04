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
using Listenarr.Application.Interfaces;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Application.Metadata;
using Listenarr.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks
{
    public class SeriesCatalogService : ISeriesCatalogService
    {
        private static readonly Dictionary<string, string> LanguageAliases = new(StringComparer.OrdinalIgnoreCase)
        {
            ["english"] = "english",
            ["en"] = "english",
            ["eng"] = "english",
            ["en-us"] = "english",
            ["en-gb"] = "english",
            ["spanish"] = "spanish",
            ["es"] = "spanish",
            ["spa"] = "spanish",
            ["es-es"] = "spanish",
            ["german"] = "german",
            ["de"] = "german",
            ["deu"] = "german",
            ["ger"] = "german",
            ["de-de"] = "german",
            ["hungarian"] = "hungarian",
            ["hu"] = "hungarian",
            ["hun"] = "hungarian",
            ["french"] = "french",
            ["fr"] = "french",
            ["fra"] = "french",
            ["fre"] = "french",
            ["fr-fr"] = "french",
            ["polish"] = "polish",
            ["pl"] = "polish",
            ["pol"] = "polish",
            ["pl-pl"] = "polish",
            ["italian"] = "italian",
            ["it"] = "italian",
            ["ita"] = "italian",
            ["it-it"] = "italian",
            ["russian"] = "russian",
            ["ru"] = "russian",
            ["rus"] = "russian",
            ["ru-ru"] = "russian",
            ["all"] = "all"
        };

        private readonly AudibleService _audibleService;
        private readonly IAudiobookRepository _audiobookRepository;
        private readonly ILogger<SeriesCatalogService> _logger;

        // Cap how many owned books we probe against Audible when recovering a series ASIN,
        // so a large library can't fan out into many metadata calls on a cache miss.
        private const int MaxOwnedBookResolutionAttempts = 3;

        public SeriesCatalogService(
            AudibleService audibleService,
            IAudiobookRepository audiobookRepository,
            ILogger<SeriesCatalogService> logger)
        {
            _audibleService = audibleService;
            _audiobookRepository = audiobookRepository;
            _logger = logger;
        }

        public async Task<SeriesCatalogFetchResult?> GetCatalogAsync(
            string name,
            string region = "us",
            int limit = 250,
            string? language = null,
            bool forceRefresh = false,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return null;
            }

            var normalizedName = name.Trim();
            var normalizedRegion = NormalizeRegion(region);
            var normalizedLanguage = NormalizeLanguage(language);
            var cachedEntry = await ResolvePersistedCacheAsync(normalizedName, normalizedRegion);

            if (!forceRefresh &&
                cachedEntry?.CatalogBooks != null &&
                cachedEntry.CatalogBooks.Count > 0)
            {
                return new SeriesCatalogFetchResult
                {
                    Series = MapCachedSeries(cachedEntry, normalizedName, normalizedRegion),
                    Books = FilterCatalogByLanguage(
                        cachedEntry.CatalogBooks.Select(MapCachedCatalogBook),
                        normalizedLanguage)
                };
            }

            var series = await ResolveSeriesAsync(normalizedName, normalizedRegion, cachedEntry);
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

            if (limitedBooks.Count == 0 &&
                cachedEntry?.CatalogBooks != null &&
                cachedEntry.CatalogBooks.Count > 0)
            {
                _logger.LogWarning(
                    "Series catalog refresh produced no books for {Series}; keeping persisted catalog cache",
                    normalizedName);

                return new SeriesCatalogFetchResult
                {
                    Series = MapCachedSeries(cachedEntry, normalizedName, normalizedRegion),
                    Books = FilterCatalogByLanguage(
                        cachedEntry.CatalogBooks.Select(MapCachedCatalogBook),
                        normalizedLanguage)
                };
            }

            if (string.IsNullOrWhiteSpace(series.Image))
            {
                series.Image = limitedBooks.FirstOrDefault(book => !string.IsNullOrWhiteSpace(book.ImageUrl))?.ImageUrl;
            }

            await PersistCatalogAsync(
                cachedEntry,
                normalizedName,
                normalizedRegion,
                series,
                limitedBooks,
                cancellationToken);

            return new SeriesCatalogFetchResult
            {
                Series = series,
                Books = FilterCatalogByLanguage(limitedBooks, normalizedLanguage)
            };
        }

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

            if (string.IsNullOrWhiteSpace(series.Image))
            {
                series.Image = limitedBooks.FirstOrDefault(book => !string.IsNullOrWhiteSpace(book.ImageUrl))?.ImageUrl;
            }

            await PersistCatalogForSlugAsync(name, normalizedRegion, series, limitedBooks, cancellationToken);

            return new SeriesCatalogFetchResult
            {
                Series = series,
                Books = FilterCatalogByLanguage(limitedBooks, normalizedLanguage)
            };
        }

        private async Task PersistCatalogForSlugAsync(
            string slug,
            string region,
            SeriesLookupItem series,
            List<AudibleSearchResult> books,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var entry = new SeriesCacheEntry
                {
                    SeriesName = string.IsNullOrWhiteSpace(series.Name) ? slug.Trim() : series.Name,
                    SeriesAsin = series.Asin,
                    Region = region,
                    ImageUrl = series.Image ?? books.FirstOrDefault(book => !string.IsNullOrWhiteSpace(book.ImageUrl))?.ImageUrl,
                    Description = series.Description,
                    CatalogBooks = books.Select(MapCachedCatalogBook).ToList(),
                    LastFetchedAt = DateTime.UtcNow
                };

                await _audiobookRepository.UpsertCachedSeriesForSlugAsync(slug, entry);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to persist selected series catalog for slug {Slug}", slug);
            }
        }

        public async Task<bool> HasCachedCatalogAsync(
            string name,
            string region = "us",
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return false;
            }

            cancellationToken.ThrowIfCancellationRequested();

            // Mirror the cache-lookup path GetCatalogAsync uses so the answer
            // matches what GetCatalogAsync would consider a cache hit.
            var cached = await ResolvePersistedCacheAsync(name.Trim(), NormalizeRegion(region));
            return cached?.CatalogBooks is { Count: > 0 };
        }

        private async Task<SeriesLookupItem?> ResolveSeriesAsync(
            string normalizedName,
            string region,
            SeriesCacheEntry? cachedEntry)
        {
            // Tier 1: a previously-resolved ASIN (persisted cache) is authoritative.
            if (cachedEntry != null && !string.IsNullOrWhiteSpace(cachedEntry.SeriesAsin))
            {
                return MapCachedSeries(cachedEntry, normalizedName, region);
            }

            // Tier 2: derive the series from a book the user already owns. This is
            // preferred over a plain name search because a stored series name can be
            // mistyped or differently formatted, in which case a name search can return
            // a confidently-wrong series (e.g. "Seekers Tale" -> the unrelated "Seekers").
            // A book we own resolves to its authoritative series ASIN via Audible's own
            // metadata for that book, so we trust it ahead of the name search.
            var fromOwnedBook = await TryResolveSeriesFromOwnedBookAsync(normalizedName, region);
            if (fromOwnedBook != null && !string.IsNullOrWhiteSpace(fromOwnedBook.Asin))
            {
                return fromOwnedBook;
            }

            // Tier 3: fall back to a name-based lookup (series we don't own a book in).
            return await _audibleService.LookupSeriesAsync(normalizedName, region);
        }

        /// <summary>
        /// Recovers a series' authoritative ASIN from a book the user already owns in that
        /// series, looking the book up on Audible by its own ASIN where possible and falling
        /// back to a title+author search. Returns null when no owned book resolves cleanly.
        /// Best-effort: any failure is logged and treated as "not recovered".
        /// </summary>
        private async Task<SeriesLookupItem?> TryResolveSeriesFromOwnedBookAsync(
            string normalizedName,
            string region)
        {
            var targetKey = NormalizeSeriesCacheKey(normalizedName);
            if (string.IsNullOrEmpty(targetKey))
            {
                return null;
            }

            try
            {
                var library = await _audiobookRepository.GetLibraryAsync() ?? new List<Audiobook>();
                var ownedBooks = library
                    .Where(book => NormalizeSeriesCacheKey(book.Series) == targetKey)
                    // Books with a known ASIN resolve most reliably; try them first.
                    .OrderByDescending(book => !string.IsNullOrWhiteSpace(book.Asin))
                    .Take(MaxOwnedBookResolutionAttempts)
                    .ToList();

                foreach (var book in ownedBooks)
                {
                    var seriesAsin = await DeriveSeriesAsinFromBookAsync(book, targetKey, region);
                    if (string.IsNullOrWhiteSpace(seriesAsin))
                    {
                        continue;
                    }

                    var series = await _audibleService.GetSeriesByAsinAsync(seriesAsin, region);
                    if (series != null && !string.IsNullOrWhiteSpace(series.Asin))
                    {
                        _logger.LogInformation(
                            "Recovered series {Series} (ASIN {SeriesAsin}) from owned book {BookAsin}",
                            normalizedName, series.Asin, book.Asin);
                        return series;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Owned-book series recovery failed for {Series}", normalizedName);
            }

            return null;
        }

        /// <summary>
        /// Resolves the ASIN of the target series from a single owned book, using the book's
        /// own ASIN where possible and falling back to a title+author search. Only an ASIN
        /// whose series name matches the target is returned, so a book that belongs to
        /// multiple series cannot resolve to the wrong one.
        /// </summary>
        private async Task<string?> DeriveSeriesAsinFromBookAsync(Audiobook book, string targetKey, string region)
        {
            var series = await GetBookSeriesCandidatesAsync(book, region);
            return MatchSeriesAsinByName(series, targetKey);
        }

        /// <summary>
        /// Returns the series an owned book belongs to according to Audible, looked up by the
        /// book's own ASIN where possible and otherwise by title+author. Used both to recover a
        /// series ASIN (with a name-match filter applied by the caller) and to surface picker
        /// candidates (no name filter). Short-circuits to the first source that yields series.
        /// </summary>
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

        private static string? MatchSeriesAsinByName(IEnumerable<AudibleSeries>? series, string targetKey)
        {
            return series?
                .FirstOrDefault(entry =>
                    !string.IsNullOrWhiteSpace(entry?.Asin) &&
                    NormalizeSeriesCacheKey(entry.Name) == targetKey)
                ?.Asin;
        }

        private async Task<SeriesCacheEntry?> ResolvePersistedCacheAsync(string normalizedName, string region)
        {
            try
            {
                return await _audiobookRepository.GetCachedSeriesByNameAsync(normalizedName, region);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to resolve persisted series catalog cache for {Series}", normalizedName);
            }

            return null;
        }

        private async Task PersistCatalogAsync(
            SeriesCacheEntry? cachedEntry,
            string seriesName,
            string region,
            SeriesLookupItem series,
            List<AudibleSearchResult> books,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var entry = cachedEntry ?? new SeriesCacheEntry();
                entry.SeriesName = string.IsNullOrWhiteSpace(series.Name) ? seriesName : series.Name;
                entry.SeriesNameNormalized = NormalizeSeriesCacheKey(seriesName);
                entry.SeriesAsin = series.Asin;
                entry.Region = region;
                entry.ImageUrl = series.Image ?? books.FirstOrDefault(book => !string.IsNullOrWhiteSpace(book.ImageUrl))?.ImageUrl ?? entry.ImageUrl;
                entry.Description = series.Description ?? entry.Description;
                entry.CatalogBooks = books.Select(MapCachedCatalogBook).ToList();
                entry.LastFetchedAt = DateTime.UtcNow;

                await _audiobookRepository.UpsertCachedSeriesAsync(entry);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to persist series catalog cache for {Series}", seriesName);
            }
        }

        private static string BuildSeriesCatalogBookKey(AudibleSearchResult book)
        {
            if (!string.IsNullOrWhiteSpace(book.Asin))
            {
                return $"asin:{NormalizeCatalogToken(book.Asin)}";
            }

            var title = NormalizeCatalogToken(book.Title);
            var authors = string.Join("|", (book.Authors ?? new List<AudibleAuthor>())
                .Select(author => NormalizeCatalogToken(author.Name))
                .Where(author => !string.IsNullOrWhiteSpace(author)));

            return $"title:{title}:authors:{authors}";
        }

        private static string NormalizeCatalogToken(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            return new string(value.Trim().ToUpperInvariant().Where(char.IsLetterOrDigit).ToArray());
        }

        private static string NormalizeRegion(string? region)
        {
            var normalized = AudiobookIdentifierNormalizer.NormalizeRegion(region);
            return string.IsNullOrWhiteSpace(normalized) ? "us" : normalized;
        }

        private static string? NormalizeLanguage(string? language)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                return null;
            }

            var trimmed = language.Trim();
            return LanguageAliases.TryGetValue(trimmed, out var normalized)
                ? normalized
                : trimmed.ToLowerInvariant();
        }

        private static List<AudibleSearchResult> FilterCatalogByLanguage(
            IEnumerable<AudibleSearchResult> books,
            string? normalizedLanguage)
        {
            var bookList = books.ToList();
            if (string.IsNullOrWhiteSpace(normalizedLanguage) ||
                string.Equals(normalizedLanguage, "all", StringComparison.OrdinalIgnoreCase))
            {
                return bookList;
            }

            return bookList
                .Where(book =>
                {
                    var bookLanguage = NormalizeLanguage(book.Language);
                    return !string.IsNullOrWhiteSpace(bookLanguage) &&
                        string.Equals(bookLanguage, normalizedLanguage, StringComparison.OrdinalIgnoreCase);
                })
                .ToList();
        }

        private static SeriesLookupItem MapCachedSeries(SeriesCacheEntry entry, string fallbackName, string region)
        {
            return new SeriesLookupItem
            {
                Asin = entry.SeriesAsin,
                Name = string.IsNullOrWhiteSpace(entry.SeriesName) ? fallbackName : entry.SeriesName,
                Image = entry.ImageUrl,
                Region = region,
                Description = entry.Description
            };
        }

        private static CachedSeriesCatalogBook MapCachedCatalogBook(AudibleSearchResult book)
        {
            var primarySeries = book.Series?.FirstOrDefault();
            var runtime = book.LengthMinutes ?? book.RuntimeLengthMin ?? book.RuntimeMinutes;

            return new CachedSeriesCatalogBook
            {
                Asin = book.Asin,
                Title = book.Title ?? "Unknown Title",
                Subtitle = book.Subtitle,
                Authors = (book.Authors ?? new List<AudibleAuthor>())
                    .Select(author => author.Name)
                    .Where(author => !string.IsNullOrWhiteSpace(author))
                    .Cast<string>()
                    .ToList(),
                ImageUrl = book.ImageUrl,
                Runtime = runtime,
                Language = book.Language,
                Publisher = book.Publisher,
                Narrators = (book.Narrators ?? new List<AudibleNarrator>())
                    .Select(narrator => narrator.Name)
                    .Where(narrator => !string.IsNullOrWhiteSpace(narrator))
                    .Cast<string>()
                    .ToList(),
                Genres = (book.Genres ?? new List<AudibleGenre>())
                    .Select(genre => genre.Name)
                    .Where(genre => !string.IsNullOrWhiteSpace(genre))
                    .Cast<string>()
                    .ToList(),
                Series = primarySeries?.Name,
                SeriesNumber = primarySeries?.Position,
                PublishedDate = book.ReleaseDate,
                Isbn = book.Isbn,
                Link = book.Link,
                MetadataSource = "Audible"
            };
        }

        private static AudibleSearchResult MapCachedCatalogBook(CachedSeriesCatalogBook book)
        {
            return new AudibleSearchResult
            {
                Asin = book.Asin,
                Title = book.Title,
                Subtitle = book.Subtitle,
                Authors = (book.Authors ?? new List<string>())
                    .Where(author => !string.IsNullOrWhiteSpace(author))
                    .Select(author => new AudibleAuthor { Name = author })
                    .ToList(),
                ImageUrl = book.ImageUrl,
                LengthMinutes = book.Runtime,
                Language = book.Language,
                Publisher = book.Publisher,
                Narrators = (book.Narrators ?? new List<string>())
                    .Where(narrator => !string.IsNullOrWhiteSpace(narrator))
                    .Select(narrator => new AudibleNarrator { Name = narrator })
                    .ToList(),
                Genres = (book.Genres ?? new List<string>())
                    .Where(genre => !string.IsNullOrWhiteSpace(genre))
                    .Select(genre => new AudibleGenre { Name = genre })
                    .ToList(),
                Series = string.IsNullOrWhiteSpace(book.Series)
                    ? null
                    : new List<AudibleSeries>
                    {
                        new()
                        {
                            Name = book.Series,
                            Position = book.SeriesNumber
                        }
                    },
                ReleaseDate = book.PublishedDate,
                Isbn = book.Isbn,
                Link = book.Link
            };
        }

        private static string NormalizeSeriesCacheKey(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var cleaned = new string(value
                .Where(character => char.IsLetterOrDigit(character) || char.IsWhiteSpace(character))
                .ToArray());
            var parts = cleaned.Split(
                new[] { ' ', '\t', '\n', '\r' },
                StringSplitOptions.RemoveEmptyEntries);

            return string.Join(' ', parts).ToLowerInvariant();
        }
    }
}
