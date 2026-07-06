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

namespace Listenarr.Application.Audiobooks.Contracts
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
        /// Returns candidate series for a name so the user can correct a wrong or
        /// ambiguous resolution. Owned-book-derived candidates rank first.
        /// </summary>
        Task<SeriesCandidateResult> GetSeriesCandidatesAsync(
            string name,
            string region = "us",
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Resolves a series catalog by an explicitly-chosen ASIN and persists it
        /// under the original name's cache slot so the choice sticks on later loads.
        /// </summary>
        Task<SeriesCatalogFetchResult?> GetCatalogByAsinAsync(
            string name,
            string asin,
            string region = "us",
            int limit = 250,
            string? language = null,
            CancellationToken cancellationToken = default);
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

    public sealed class SeriesCatalogFetchResult
    {
        public SeriesLookupItem Series { get; set; } = new();

        public List<AudibleSearchResult> Books { get; set; } = new();

        public int TotalBooks => Books.Count;
    }
}
