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

namespace Listenarr.Application.Interfaces
{
    public interface ILibraryAddService
    {
        Task<LibraryAddOperationResult> AddToLibraryAsync(
            LibraryAddOperationRequest request,
            CancellationToken cancellationToken = default);
    }

    public sealed class LibraryAddOperationRequest
    {
        public AudibleBookMetadata Metadata { get; set; } = new();

        public bool Monitored { get; set; } = true;

        public int? QualityProfileId { get; set; }

        public bool AutoSearch { get; set; }

        public string? DestinationPath { get; set; }

        public SearchResult? SearchResult { get; set; }

        public string HistorySource { get; set; } = "AddNew";

        public string? HistoryMessage { get; set; }

        /// <summary>
        /// When true, the service skips its ASIN/ISBN dedup check and creates a new audiobook
        /// record even if one with the same identifiers already exists. Used by the file
        /// extraction "Make a duplicate" flow where the caller has explicitly chosen to keep
        /// both copies. Defaults to false so the standard add-to-library behaviour is
        /// unchanged.
        /// </summary>
        public bool BypassDuplicateCheck { get; set; }
    }

    public sealed class LibraryAddOperationResult
    {
        public bool Added { get; set; }

        public bool AlreadyExists { get; set; }

        public string Message { get; set; } = string.Empty;

        public Audiobook? Audiobook { get; set; }
    }
}
