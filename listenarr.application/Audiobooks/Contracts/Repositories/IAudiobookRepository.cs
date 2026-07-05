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

namespace Listenarr.Application.Audiobooks.Contracts.Repositories
{
    public interface IAudiobookRepository
    {
        Task<List<Audiobook>> GetAllAsync();
        Task<List<Audiobook>> GetLibraryAsync();
        Task<Dictionary<int, List<AudiobookSeriesMembership>>> GetAllSeriesMembershipsGroupedByAudiobookIdAsync(CancellationToken ct = default);
        Task<List<Audiobook>> GetByIdsWithFilesAsync(IEnumerable<int> ids, CancellationToken ct = default);
        Task<List<Audiobook>> GetMonitoredAudiobooksForSearchAsync(DateTime cutoff, CancellationToken ct = default);
        Task NormalizeJsonColumnsAsync(CancellationToken ct = default);
        Task<Audiobook?> GetByAsinAsync(string asin);
        Task<Audiobook?> GetByIsbnAsync(string isbn);
        Task<Audiobook?> GetByIdAsync(int id);
        Task<string?> GetAuthorAsinByNameAsync(string name);
        Task<AuthorCacheEntry?> GetCachedAuthorByNameAsync(string name, string region);
        Task<AuthorCacheEntry?> GetCachedAuthorByAsinAsync(string asin, string region);
        Task<AuthorCacheEntry> UpsertCachedAuthorAsync(AuthorCacheEntry authorCacheEntry);
        Task<SeriesCacheEntry?> GetCachedSeriesByNameAsync(string name, string region);
        Task<SeriesCacheEntry?> GetCachedSeriesByAsinAsync(string asin, string region);
        Task<SeriesCacheEntry> UpsertCachedSeriesAsync(SeriesCacheEntry seriesCacheEntry);

        /// <summary>
        /// Cached Audible catalog sizes for the given series names, keyed by the
        /// caller's own (raw) name. Names are normalized internally with the same
        /// rules the cache rows were written with, so callers never need to know
        /// the normalization. Only series with a cached catalog of at least one
        /// book appear in the result; the freshest cache row wins per name.
        /// </summary>
        Task<Dictionary<string, int>> GetSeriesCatalogTotalsAsync(IReadOnlyCollection<string> seriesNames, string region, CancellationToken ct = default);
        Task<Audiobook> AddAsync(Audiobook audiobook);
        Task<bool> UpdateAsync(Audiobook audiobook);

        /// <summary>
        /// Stamp only <see cref="Audiobook.LastSearchTime"/> without touching any
        /// other column. The automatic-search cycle iterates a snapshot loaded at
        /// cycle start, sometimes for a long time — saving the whole stale entity
        /// to bump the timestamp clobbered every concurrent change to the row
        /// (live case: a verification reset reverted minutes after it was made).
        /// </summary>
        Task SetLastSearchTimeAsync(int audiobookId, DateTime lastSearchTimeUtc);

        /// <summary>
        /// Targeted single-column ImageUrl write. The cover-art sweep walks a
        /// snapshot taken at sweep start; re-saving whole entities from it would
        /// clobber concurrent edits (same rationale as SetLastSearchTimeAsync).
        /// </summary>
        Task SetImageUrlAsync(int audiobookId, string? imageUrl);
        Task<bool> DeleteAsync(Audiobook audiobook);
        Task<bool> DeleteByIdAsync(int id);
        Task<int> DeleteBulkAsync(List<int> ids);
        Task SaveChangesAsync(CancellationToken ct = default);
        Task<bool> UpdateWithIdentifierReplaceAsync(Audiobook audiobook, List<AudiobookExternalIdentifier> newIdentifiers, CancellationToken ct = default);
    }
}
