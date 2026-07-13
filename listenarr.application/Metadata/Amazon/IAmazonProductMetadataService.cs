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
using Listenarr.Application.Metadata.Audible;

namespace Listenarr.Application.Metadata.Amazon
{
    /// <summary>
    /// Last-resort metadata source: scrapes an Amazon product page for an
    /// audiobook that no Audible catalog carries. Amazon sells audiobooks
    /// (including Audible-format ones) that never appear in any regional
    /// Audible store — live case: an Amazon-exclusive HarperVoyager edition
    /// resolvable at amazon.com/dp/&lt;asin&gt; while all ten Audible catalog
    /// APIs return an empty stub for the same ASIN.
    ///
    /// Results are mapped into <see cref="AudibleBookResponse"/> so every
    /// downstream consumer (metadata backfill, extraction, add flows) works
    /// unchanged. Scraping is inherently fragile — implementations return
    /// null on robot walls, markup drift, or anything short of a confident
    /// parse, and callers treat null as "source has nothing".
    /// </summary>
    public interface IAmazonProductMetadataService
    {
        Task<AudibleBookResponse?> GetBookMetadataAsync(string asin, CancellationToken cancellationToken = default);
    }
}
