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
    /// One same-ASIN group surfaced by the duplicates preview endpoint.
    /// </summary>
    public class DuplicateGroupDto
    {
        /// <summary>The uppercase-trimmed ASIN shared by every row in the group.</summary>
        public string NormalizedAsin { get; set; } = string.Empty;

        /// <summary>The library rows that share this ASIN.</summary>
        public List<DuplicateRowDto> Rows { get; set; } = new();
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
    }

    /// <summary>
    /// Request body for the merge endpoint. Each entry says "keep
    /// <see cref="MergePairDto.WinnerId"/>, delete each id in
    /// <see cref="MergePairDto.LoserIds"/> after reassigning their references."
    /// </summary>
    public class MergeDuplicatesRequest
    {
        public List<MergePairDto> Merges { get; set; } = new();
    }

    public class MergePairDto
    {
        public int WinnerId { get; set; }
        public List<int> LoserIds { get; set; } = new();
    }

    /// <summary>
    /// Counts returned by the merge endpoint so the UI can summarize what
    /// happened.
    /// </summary>
    public class MergeDuplicatesResultDto
    {
        public int GroupsProcessed { get; set; }
        public int RowsDeleted { get; set; }
        public int DownloadsReassigned { get; set; }
        public int HistoryReassigned { get; set; }
        public int MoveJobsReassigned { get; set; }
        public List<string> Warnings { get; set; } = new();
    }
}
