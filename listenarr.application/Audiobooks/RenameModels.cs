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
namespace Listenarr.Application.Audiobooks
{
    public class BulkRenameRequest
    {
        public int[] AudiobookIds { get; set; } = Array.Empty<int>();
    }

    public class RenameOperation
    {
        public int AudiobookId { get; set; }
        public string? NewFolderPath { get; set; }
        public List<FileRenameOperation> FileRenames { get; set; } = new();
    }

    public class FileRenameOperation
    {
        public int FileId { get; set; }
        public string CurrentPath { get; set; } = string.Empty;
        public string NewPath { get; set; } = string.Empty;

        /// <summary>
        /// How to resolve a "target file already exists" collision for this
        /// file. Defaults to <see cref="ConflictResolution.Skip"/> — the
        /// historical behavior, which now surfaces the conflict metadata so
        /// the UI can show a resolver dialog instead of dead-ending.
        /// Record-level resolutions ("delete the duplicate book", "let the
        /// incoming book win") are handled in the UI via the existing
        /// <c>DELETE /library/{id}</c> endpoint; the only file-level option
        /// here is <see cref="ConflictResolution.Overwrite"/>, and it is
        /// honored only when the destination file is an untracked orphan
        /// (no audiobook record owns it).
        /// </summary>
        public ConflictResolution OnConflict { get; set; } = ConflictResolution.Skip;
    }

    /// <summary>
    /// Per-file resolution for a destination-already-exists collision during
    /// organize.
    /// </summary>
    public enum ConflictResolution
    {
        /// <summary>Don't move; return conflict metadata for the UI (default).</summary>
        Skip = 0,

        /// <summary>
        /// Delete the existing destination file, then move the source over it.
        /// Only honored when the destination is an untracked orphan; refused
        /// (returns a conflict) when another audiobook record tracks the
        /// destination, since that would orphan the other record — the UI
        /// resolves that case at the record level instead.
        /// </summary>
        Overwrite = 1
    }

    public class ExecuteRenameRequest
    {
        public List<RenameOperation> Operations { get; set; } = new();
    }

    public class RenamePreview
    {
        public int AudiobookId { get; set; }
        public string? AudiobookTitle { get; set; }
        public string? CurrentFolderPath { get; set; }
        public string? NewFolderPath { get; set; }
        public bool FolderChanged { get; set; }
        public List<FileRenamePreview> FileRenames { get; set; } = new();
        public bool HasChanges { get; set; }
    }

    public class FileRenamePreview
    {
        public int FileId { get; set; }
        public string? CurrentPath { get; set; }
        public string? NewPath { get; set; }
        public string? CurrentFilename { get; set; }
        public string? NewFilename { get; set; }
        public bool Changed { get; set; }
    }

    public class RenameResult
    {
        public int AudiobookId { get; set; }
        public bool Success { get; set; }
        public string? Error { get; set; }
        public List<FileRenameResultItem> RenamedFiles { get; set; } = new();
    }

    public class FileRenameResultItem
    {
        public int FileId { get; set; }
        public string? PreviousPath { get; set; }
        public string? NewPath { get; set; }
        public bool Success { get; set; }
        public string? Error { get; set; }

        /// <summary>
        /// True when this item failed because the destination already exists.
        /// When set, <see cref="Conflict"/> carries the side-by-side metadata
        /// the UI needs to offer Overwrite / Delete-duplicate / Skip.
        /// </summary>
        public bool IsConflict { get; set; }

        /// <summary>Populated when <see cref="IsConflict"/> is true.</summary>
        public RenameConflictInfo? Conflict { get; set; }
    }

    /// <summary>
    /// Side-by-side metadata for a destination-already-exists collision, so the
    /// UI can present the incoming file and the existing file (and, when the
    /// existing file is tracked, the audiobook record that owns it) and let the
    /// user decide what to do.
    /// </summary>
    public class RenameConflictInfo
    {
        /// <summary>The file we were trying to move into place.</summary>
        public ConflictFileInfo Incoming { get; set; } = new();

        /// <summary>The file already sitting at the destination path.</summary>
        public ConflictFileInfo Existing { get; set; } = new();

        /// <summary>
        /// True when an audiobook record tracks the existing destination file.
        /// When true, the safe resolutions are record-level (delete the
        /// duplicate book, or let the incoming book win) rather than a blind
        /// file overwrite.
        /// </summary>
        public bool ExistingTracked { get; set; }

        /// <summary>The audiobook id that owns the existing file, if tracked.</summary>
        public int? ExistingAudiobookId { get; set; }

        /// <summary>The title of the audiobook that owns the existing file, if tracked.</summary>
        public string? ExistingAudiobookTitle { get; set; }

        /// <summary>The tracked file id of the existing destination file, if tracked.</summary>
        public int? ExistingFileId { get; set; }
    }

    /// <summary>
    /// Lightweight file descriptor for the conflict comparison UI. Values come
    /// from the tracked <c>AudiobookFiles</c> row when available, with on-disk
    /// <c>FileInfo</c> filling size/modified time.
    /// </summary>
    public class ConflictFileInfo
    {
        public string? Path { get; set; }
        public long? Size { get; set; }
        public double? DurationSeconds { get; set; }
        public string? Format { get; set; }
        public string? Container { get; set; }
        public string? Codec { get; set; }
        public int? Bitrate { get; set; }
        public DateTime? ModifiedAt { get; set; }
    }
}
