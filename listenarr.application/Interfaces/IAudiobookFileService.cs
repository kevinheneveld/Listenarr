using Listenarr.Domain.Models;

namespace Listenarr.Application.Interfaces
{
    /// <summary>
    /// Manages audio file metadata extraction and database tracking
    /// </summary>
    public interface IAudiobookFileService
    {
        /// <summary>
        /// Ensure an Audiobook file record exists for the given audiobook and file path. Extract metadata (ffprobe/taglib) and persist file-level metadata.
        /// When <paramref name="forceMetadataRefresh"/> is true, metadata is re-extracted and library-level fields
        /// on the parent audiobook are backfilled even when the file is already tracked.
        /// </summary>
        /// <param name="audiobook">The audiobook</param>
        /// <param name="filePath">Path to the audio file</param>
        /// <param name="source">Optional source identifier (e.g., "scan", "import")</param>
        /// <param name="forceMetadataRefresh">If true, re-extract metadata and backfill blank audiobook fields even for already-tracked files</param>
        /// <returns>True if a new AudiobookFile record was created, false if it already existed or was rejected</returns>
        Task<bool> EnsureAudiobookFileAsync(Audiobook audiobook, string filePath, string? source = "scan", bool forceMetadataRefresh = false);
    }
}
