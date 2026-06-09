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
using Listenarr.Domain.Models;

namespace Listenarr.Application.Interfaces.Repositories
{
    public interface IAudiobookRepository
    {
        Task<List<Audiobook>> GetAllAsync();
        Task<List<Audiobook>> GetLibraryAsync();
        Task<Dictionary<int, List<AudiobookSeriesMembership>>> GetAllSeriesMembershipsGroupedByAudiobookIdAsync(CancellationToken ct = default);
        Task<List<Audiobook>> GetByIdsWithFilesAsync(IEnumerable<int> ids, CancellationToken ct = default);
        /// <summary>
        /// Returns the audiobook (with its <see cref="Audiobook.Files"/> loaded)
        /// that tracks a file at the given normalized path, or null if none
        /// does. Read-only; used by organize to identify which record owns a
        /// destination file when a rename would collide.
        /// </summary>
        Task<Audiobook?> GetByFilePathAsync(string normalizedPath, CancellationToken ct = default);
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
        /// Upserts a series cache entry keyed strictly by the normalized <paramref name="slug"/>
        /// (the name the series page was reached by), independent of the resolved series name or
        /// ASIN. Used when the user explicitly picks the correct series for a slug whose default
        /// resolution was wrong — it overwrites the existing slug-keyed row so the pick sticks,
        /// rather than matching on the resolved series' ASIN/name like <see cref="UpsertCachedSeriesAsync"/>.
        /// </summary>
        Task<SeriesCacheEntry> UpsertCachedSeriesForSlugAsync(string slug, SeriesCacheEntry entry);
        Task<Audiobook> AddAsync(Audiobook audiobook);
        Task<bool> UpdateAsync(Audiobook audiobook);
        Task<bool> DeleteAsync(Audiobook audiobook);
        Task<bool> DeleteByIdAsync(int id);
        Task<int> DeleteBulkAsync(List<int> ids);
        Task SaveChangesAsync(CancellationToken ct = default);
        Task<bool> UpdateWithIdentifierReplaceAsync(Audiobook audiobook, List<AudiobookExternalIdentifier> newIdentifiers, CancellationToken ct = default);
    }
}
