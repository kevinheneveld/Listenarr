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
using Listenarr.Application.Metadata;

namespace Listenarr.Application.Interfaces
{
    public interface ISeriesCatalogService
    {
        Task<SeriesCatalogFetchResult?> GetCatalogAsync(
            string name,
            string region = "us",
            int limit = 250,
            string? language = null,
            bool forceRefresh = false,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// True when a non-empty catalog for this series is already persisted in
        /// the cache — lets callers skip series that don't need a fetch without
        /// triggering one.
        /// </summary>
        Task<bool> HasCachedCatalogAsync(
            string name,
            string region = "us",
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Returns candidate series for a (possibly mistyped/mis-parsed) series name so the
        /// user can pick the correct one. Candidates derived from books the user already owns
        /// in the series rank first (and carry the real Audible series even when its name
        /// doesn't match the supplied slug), followed by name-search matches.
        /// </summary>
        Task<SeriesCandidateResult> GetSeriesCandidatesAsync(
            string name,
            string region = "us",
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Resolves the catalog for an explicitly-chosen series ASIN and persists it against
        /// the supplied <paramref name="name"/> (slug), overwriting any prior — possibly
        /// wrong — cache entry for that slug so the user's choice sticks on later loads.
        /// </summary>
        Task<SeriesCatalogFetchResult?> GetCatalogByAsinAsync(
            string name,
            string asin,
            string region = "us",
            int limit = 250,
            string? language = null,
            CancellationToken cancellationToken = default);
    }

    public sealed class SeriesCatalogFetchResult
    {
        public SeriesLookupItem Series { get; set; } = new();

        public List<AudibleSearchResult> Books { get; set; } = new();

        public int TotalBooks => Books.Count;
    }

    /// <summary>A single pickable series candidate.</summary>
    public sealed class SeriesCandidate
    {
        public string Asin { get; set; } = string.Empty;

        public string? Name { get; set; }

        public string? Image { get; set; }

        public int? BookCount { get; set; }

        /// <summary>"library" when derived from a book the user owns, otherwise "audible".</summary>
        public string Source { get; set; } = "audible";

        /// <summary>How many owned books in the collection point at this series.</summary>
        public int OwnedMatchCount { get; set; }
    }

    public sealed class SeriesCandidateResult
    {
        public string Query { get; set; } = string.Empty;

        public string? BestGuessAsin { get; set; }

        public List<SeriesCandidate> Candidates { get; set; } = new();
    }
}
