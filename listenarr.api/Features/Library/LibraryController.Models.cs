/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

namespace Listenarr.Api.Features.Library;

public partial class LibraryController
{
    public class ScanRequest
    {
        public string? Path { get; set; }

        /// <summary>
        /// If true, re-extract metadata for already-tracked files and backfill blank
        /// library-level fields on the audiobook record (cover, ASIN, ISBN, series,
        /// narrator, etc.). Existing non-blank values are never overwritten.
        /// </summary>
        public bool ForceMetadataRefresh { get; set; }
    }

    public class BulkDeleteRequest
    {
        public List<int> Ids { get; set; } = [];
    }

    public class BulkUpdateRequest
    {
        public List<int> Ids { get; set; } = [];
        public Dictionary<string, object> Updates { get; set; } = [];
    }

    public class AddToLibraryRequest
    {
        public AudibleBookMetadata Metadata { get; set; } = new();
        public bool Monitored { get; set; } = true;
        public int? QualityProfileId { get; set; }
        public bool AutoSearch { get; set; }
        public string? DestinationPath { get; set; }
        public SearchResult? SearchResult { get; set; }
    }

    public class PreviewPathRequest
    {
        public AudibleBookMetadata Metadata { get; set; } = new();
        public string? DestinationRoot { get; set; }
    }

    public class MoveRequest
    {
        public string? DestinationPath { get; set; }
        public string? SourcePath { get; set; }
        public bool? MoveFiles { get; set; }
        public bool? DeleteEmptySource { get; set; }
    }

    /// <summary>
    /// Apply duplicate-copy proposals: each item names one record and the
    /// exact redundant file ids from the analysis. The server re-verifies
    /// against a fresh analysis before deleting anything.
    /// </summary>
    public class ApplyDuplicateCopiesRequest
    {
        public List<ApplyDuplicateCopyItem> Applications { get; set; } = [];
    }

    public class ApplyDuplicateCopyItem
    {
        public int AudiobookId { get; set; }
        public List<int> RedundantFileIds { get; set; } = [];
    }

    public class MergeDuplicatesRequest
    {
        public List<MergeDuplicatesPair> Merges { get; set; } = [];
    }

    /// <summary>
    /// One resolution decision: losers merge into the winner (same-ASIN rows),
    /// clear-ASIN ids keep their row but lose the ASIN (same ASIN, different book).
    /// </summary>
    public class MergeDuplicatesPair
    {
        public int? WinnerId { get; set; }
        public List<int>? LoserIds { get; set; }
        public List<int>? ClearAsinIds { get; set; }
    }

    public class MergeDuplicatesResult
    {
        public int GroupsProcessed { get; set; }
        public int RowsDeleted { get; set; }
        public int AsinsCleared { get; set; }
        public int DownloadsReassigned { get; set; }
        public int HistoryReassigned { get; set; }
        public int MoveJobsReassigned { get; set; }
        public int DiskFilesDeleted { get; set; }
        public int DiskFoldersDeleted { get; set; }
        public List<string> Warnings { get; set; } = [];
    }

    /// <summary>
    /// Resolve a metadata-apply ASIN conflict (PUT /library/{id} returning 409
    /// asin_conflict): unlike duplicates/merge, the two rows don't need to
    /// already share an ASIN — the conflict itself is the proof they're
    /// duplicates. keepThisRecord picks which side survives; the loser's
    /// files are deleted from disk (same semantics as duplicates/merge).
    /// </summary>
    public class ResolveAsinConflictRequest
    {
        public int ConflictingAudiobookId { get; set; }
        public bool KeepThisRecord { get; set; }
    }

    public class ResolveAsinConflictResult
    {
        public int WinnerId { get; set; }
        public int LoserId { get; set; }
        public int DiskFilesDeleted { get; set; }
        public bool DiskFolderDeleted { get; set; }
        public List<string> Warnings { get; set; } = [];
    }

    public class TransferFilesRequest
    {
        public int TargetAudiobookId { get; set; }

        /// <summary>Files to move; null/empty transfers every file on the source.</summary>
        public List<int>? FileIds { get; set; }
    }
}
