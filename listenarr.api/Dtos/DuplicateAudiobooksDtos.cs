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
    /// Bucket-kind discriminator for <see cref="DuplicateGroupDto.Kind"/>.
    /// </summary>
    public static class DuplicateGroupKind
    {
        /// <summary>Rows share the same normalized ASIN.</summary>
        public const string Asin = "asin";
        /// <summary>
        /// Rows compute to the same canonical folder target via
        /// <c>FolderNamingPattern</c> (same <c>{Author}/{Title}/…</c>) but have
        /// distinct ASINs. Surfaces edition-variant duplicates and "wrong
        /// metadata" rows the same-ASIN dedup pass cannot catch.
        /// </summary>
        public const string TitleAuthor = "title_author";
    }

    /// <summary>
    /// One duplicate group surfaced by the duplicates preview endpoint. May be
    /// keyed by ASIN or by computed folder target — see <see cref="Kind"/>.
    /// </summary>
    public class DuplicateGroupDto
    {
        /// <summary>
        /// One of <see cref="DuplicateGroupKind"/>. Defaults to <c>asin</c>
        /// for back-compat with older clients that don't know about the
        /// title/author pass.
        /// </summary>
        public string Kind { get; set; } = DuplicateGroupKind.Asin;

        /// <summary>The uppercase-trimmed ASIN shared by every row in the group. Empty for non-ASIN groups.</summary>
        public string NormalizedAsin { get; set; } = string.Empty;

        /// <summary>
        /// The normalized canonical-folder target the rows in this group all
        /// compute to. Populated when <see cref="Kind"/> is <c>title_author</c>;
        /// empty otherwise. Lets the UI present the grouping criterion.
        /// </summary>
        public string CollisionKey { get; set; } = string.Empty;

        /// <summary>The library rows in this group.</summary>
        public List<DuplicateRowDto> Rows { get; set; } = new();

        /// <summary>
        /// Human-readable explanation of why the row marked
        /// <see cref="DuplicateRowDto.RecommendedWinner"/> was chosen, or — for
        /// <c>title_author</c> groups — guidance on how to act on the group.
        /// Empty when no row is recommended.
        /// </summary>
        public string RecommendationReason { get; set; } = string.Empty;
    }

    /// <summary>
    /// One row inside a <see cref="DuplicateGroupDto"/>.
    /// </summary>
    public class DuplicateRowDto
    {
        public int Id { get; set; }
        public string? Title { get; set; }
        public string? Series { get; set; }
        public string? SeriesNumber { get; set; }
        public string? Asin { get; set; }
        public string? BasePath { get; set; }
        public string? FilePath { get; set; }
        public string? ImageUrl { get; set; }
        /// <summary>Number of rows in <c>AudiobookFiles</c> with this <c>AudiobookId</c>.</summary>
        public int FileCount { get; set; }
        /// <summary>True when the row has a single-file path OR a tracked-file row.</summary>
        public bool HasAnyFile { get; set; }
        /// <summary>
        /// True when <see cref="BasePath"/> looks like a real audiobook folder
        /// (not the root <c>/audiobooks</c> or a bare author-folder path).
        /// </summary>
        public bool HasBookFolder { get; set; }
        /// <summary>
        /// Server-suggested winner for the group. True for at most one row per
        /// group. Heuristic: prefer rows with files, then rows with a real
        /// book folder, then the lowest Id.
        /// </summary>
        public bool RecommendedWinner { get; set; }

        // --- Extended metadata so the UI can verify two rows are really the
        // same book before merging. ---

        public List<string> Authors { get; set; } = new();
        public List<string> Narrators { get; set; } = new();
        /// <summary>Runtime in minutes, when known.</summary>
        public int? Runtime { get; set; }
        /// <summary>Tracked file rows for this audiobook.</summary>
        public List<DuplicateFileDto> Files { get; set; } = new();
        /// <summary>Sum of <see cref="DuplicateFileDto.Size"/> across <see cref="Files"/>.</summary>
        public long TotalSize { get; set; }
        /// <summary>
        /// Files that share a normalized filename signature with at least one
        /// other file on the same row — i.e. likely the same chapter imported
        /// twice in different naming styles. <c>FileCount - LikelyDuplicateFileCount</c>
        /// gives the effective unique chapter count.
        /// </summary>
        public int LikelyDuplicateFileCount { get; set; }
    }

    /// <summary>
    /// Minimal projection of an <c>AudiobookFiles</c> row for the duplicates UI.
    /// </summary>
    public class DuplicateFileDto
    {
        public int Id { get; set; }
        public string? Path { get; set; }
        public long? Size { get; set; }
        public double? DurationSeconds { get; set; }
        public string? Format { get; set; }
        public string? Codec { get; set; }
        public int? Bitrate { get; set; }
    }

    /// <summary>
    /// Request body for the merge endpoint. Each pair describes one group's
    /// resolution: zero-or-one <see cref="MergePairDto.WinnerId"/>, plus the
    /// rows to merge into it (<see cref="MergePairDto.LoserIds"/>) and/or the
    /// rows whose ASIN should be cleared so they're no longer duplicates
    /// (<see cref="MergePairDto.ClearAsinIds"/>).
    /// </summary>
    /// <remarks>
    /// Rows in the group that appear in none of these lists are skipped — the
    /// endpoint leaves them untouched.
    /// </remarks>
    public class MergeDuplicatesRequest
    {
        public List<MergePairDto> Merges { get; set; } = new();
    }

    public class MergePairDto
    {
        /// <summary>
        /// The surviving row id. May be null when the only action for this
        /// group is to clear ASINs on selected rows (no merge).
        /// </summary>
        public int? WinnerId { get; set; }
        /// <summary>Rows to delete and reassign-FKs into the winner.</summary>
        public List<int> LoserIds { get; set; } = new();
        /// <summary>Rows to keep but null out the ASIN on.</summary>
        public List<int> ClearAsinIds { get; set; } = new();
    }

    /// <summary>
    /// Counts returned by the merge endpoint so the UI can summarize what
    /// happened.
    /// </summary>
    public class MergeDuplicatesResultDto
    {
        public int GroupsProcessed { get; set; }
        public int RowsDeleted { get; set; }
        public int AsinsCleared { get; set; }
        public int DownloadsReassigned { get; set; }
        public int HistoryReassigned { get; set; }
        public int MoveJobsReassigned { get; set; }
        /// <summary>Files deleted from disk during Discard cleanup.</summary>
        public int DiskFilesDeleted { get; set; }
        /// <summary>Book folders deleted from disk during Discard cleanup.</summary>
        public int DiskFoldersDeleted { get; set; }
        /// <summary>Empty parent (e.g. author) folders cleaned up during Discard.</summary>
        public int DiskParentFoldersDeleted { get; set; }
        public List<string> Warnings { get; set; } = new();
    }
}
