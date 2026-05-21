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
using System.Text.Json.Serialization;
using Listenarr.Domain.Models;

namespace Listenarr.Application.Audiobooks
{
    /// <summary>
    /// Disambiguation strategy when the extract target's ASIN already matches an existing
    /// audiobook in the library. <see cref="None"/> is the sentinel for "user hasn't picked
    /// yet" — the service returns a conflict and the caller is expected to re-issue the
    /// request with one of the resolved strategies.
    /// Annotated with <see cref="JsonStringEnumConverter"/> explicitly so the value
    /// round-trips as a string regardless of whether a host has the converter registered
    /// globally (the API already does, but this defends against misconfiguration). On the
    /// wire the values are the PascalCase enum names ("None", "Merge", "Duplicate"); reads
    /// are case-insensitive so the FE can send lowercase if it prefers.
    /// </summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum DuplicateStrategy
    {
        None = 0,
        Merge = 1,
        Duplicate = 2,
    }

    /// <summary>
    /// Embedded metadata read from a single file's container tags (ffprobe). Used by the
    /// extract UI to seed the Audible candidate search and to display "what this file
    /// claims to be" alongside the chosen Audible payload.
    /// </summary>
    public class EmbeddedFileMetadata
    {
        public int FileId { get; set; }
        public int AudiobookId { get; set; }
        public string? CurrentPath { get; set; }
        public string? Title { get; set; }
        public string? Subtitle { get; set; }
        public string? Author { get; set; }
        public string? AlbumArtist { get; set; }
        public string? Narrator { get; set; }
        public string? Album { get; set; }
        public string? Description { get; set; }
        public string? Genre { get; set; }
        public int? Year { get; set; }
        public string? Asin { get; set; }
        public string? Isbn { get; set; }
        public string? Series { get; set; }
        public decimal? SeriesPosition { get; set; }
        public double? DurationSeconds { get; set; }
        public int? BitRate { get; set; }
        public string? Format { get; set; }
    }

    public class ExtractFileRequest
    {
        public AudibleBookMetadata Metadata { get; set; } = new();
        public DuplicateStrategy DuplicateStrategy { get; set; } = DuplicateStrategy.None;
        public int? QualityProfileId { get; set; }
        public bool Monitored { get; set; } = true;
        public bool CacheImageLocally { get; set; }
    }

    public class ExtractFileConflict
    {
        public int ExistingAudiobookId { get; set; }
        public string? ExistingTitle { get; set; }
        public string? ExistingAsin { get; set; }
        public int ExistingFileCount { get; set; }
        public string RecommendedStrategy { get; set; } = "merge";
        public string? RecommendationReason { get; set; }
    }

    public class ExtractFileResult
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public DuplicateStrategy AppliedStrategy { get; set; }
        public int? DestinationAudiobookId { get; set; }
        public string? DestinationAudiobookTitle { get; set; }
        public string? NewFilePath { get; set; }
        public int SourceAudiobookId { get; set; }
        public bool SourceAudiobookEmpty { get; set; }
        public ExtractFileConflict? Conflict { get; set; }
    }
}
