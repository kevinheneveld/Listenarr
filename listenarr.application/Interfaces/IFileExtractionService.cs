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
using Listenarr.Application.Audiobooks;

namespace Listenarr.Application.Interfaces
{
    /// <summary>
    /// Move a single tracked audiobook file out of its current audiobook and onto
    /// a different (typically new) one whose metadata better matches the file's
    /// actual content.
    /// </summary>
    public interface IFileExtractionService
    {
        /// <summary>
        /// Read embedded container tags (ffprobe) from the file. Used to seed the
        /// extract UI's Audible candidate search and to display alongside Audible's
        /// payload for comparison.
        /// </summary>
        Task<EmbeddedFileMetadata?> ReadEmbeddedAsync(int audiobookId, int fileId, CancellationToken ct = default);

        /// <summary>
        /// Extract a single file from its current audiobook into a destination audiobook
        /// constructed from the supplied <see cref="ExtractFileRequest.Metadata"/>. If the
        /// metadata's ASIN already matches an existing audiobook, the caller must pick a
        /// <see cref="DuplicateStrategy"/>; passing <see cref="DuplicateStrategy.None"/> in
        /// that situation returns a result with <see cref="ExtractFileResult.Conflict"/>
        /// populated and no side effects.
        /// </summary>
        Task<ExtractFileResult> ExtractToNewAudiobookAsync(
            int sourceAudiobookId,
            int fileId,
            ExtractFileRequest request,
            CancellationToken ct = default);
    }
}
