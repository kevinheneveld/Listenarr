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

namespace Listenarr.Application.Search.Contracts
{
    /// <summary>
    /// Runs the automatic-search pipeline for a single book on demand (e.g.
    /// right after wrong content was purged), outside the periodic sweep.
    /// </summary>
    public interface IAutomaticSearchInvoker
    {
        Task<AutomaticSearchBookResult> SearchAudiobookNowAsync(int audiobookId, CancellationToken ct = default);
    }

    public sealed class AutomaticSearchBookResult
    {
        public int AudiobookId { get; set; }
        public string Title { get; set; } = string.Empty;
        /// <summary>True when the book was processed without error (not whether a download was queued).</summary>
        public bool Success { get; set; }
        public int DownloadsQueued { get; set; }
        public string? Message { get; set; }
    }
}
