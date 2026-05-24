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

namespace Listenarr.Api.Dtos
{
    /// <summary>
    /// Bucket assigned to each audiobook in the organize-library preview.
    /// </summary>
    public static class OrganizePreviewStatus
    {
        public const string AlreadyCanonical = "already_canonical";
        public const string WillMove = "will_move";
        public const string Collision = "collision";
        public const string InvalidTarget = "invalid_target";
    }

    /// <summary>
    /// One row in the organize-library preview. Computed by applying the
    /// configured <c>FolderNamingPattern</c> to the audiobook's metadata and
    /// comparing the resulting path against the current <c>BasePath</c>.
    /// </summary>
    public class OrganizePreviewRowDto
    {
        public int Id { get; set; }
        public string? Title { get; set; }
        public string? Author { get; set; }
        public string? CurrentPath { get; set; }
        public string? TargetPath { get; set; }
        public int FileCount { get; set; }
        public long TotalSize { get; set; }
        /// <summary>One of <see cref="OrganizePreviewStatus"/>.</summary>
        public string Status { get; set; } = string.Empty;
        /// <summary>
        /// Set when <see cref="Status"/> is <c>collision</c> — the normalized
        /// target path shared by multiple audiobooks. Lets the UI group
        /// collision rows together.
        /// </summary>
        public string? CollisionKey { get; set; }
        /// <summary>
        /// Set when <see cref="Status"/> is <c>invalid_target</c> — the
        /// reason the target couldn't be computed (e.g. "Missing author").
        /// </summary>
        public string? Reason { get; set; }
    }

    public class OrganizeLibraryPreviewDto
    {
        public List<OrganizePreviewRowDto> Rows { get; set; } = new();
        public int AlreadyCanonicalCount { get; set; }
        public int WillMoveCount { get; set; }
        public int CollisionCount { get; set; }
        public int InvalidTargetCount { get; set; }
    }

    /// <summary>
    /// Apply-endpoint body. Caller passes the explicit ids confirmed in the
    /// preview UI; the server re-validates each one before queuing.
    /// </summary>
    public class OrganizeLibraryApplyRequest
    {
        public List<int> AudiobookIds { get; set; } = new();
    }

    public class OrganizeApplySkippedDto
    {
        public int AudiobookId { get; set; }
        public string Reason { get; set; } = string.Empty;
    }

    public class OrganizeLibraryApplyResultDto
    {
        public int Queued { get; set; }
        public int Skipped { get; set; }
        public int FailedToQueue { get; set; }
        public List<string> JobIds { get; set; } = new();
        public List<OrganizeApplySkippedDto> SkippedDetails { get; set; } = new();
        public List<string> Warnings { get; set; } = new();
    }
}
