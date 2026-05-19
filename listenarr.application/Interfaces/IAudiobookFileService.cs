using Listenarr.Application.Audiobooks;
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
        /// </summary>
        /// <param name="audiobook">The audiobook</param>
        /// <param name="filePath">Path to the audio file</param>
        /// <param name="source">Optional source identifier (e.g., "scan", "import")</param>
        /// <returns>True if the audiobook is associated with an audiobook file, false otherwise</returns>
        Task<bool> EnsureAudiobookFileAsync(Audiobook audiobook, string filePath, string? source = "scan");

        /// <summary>
        /// Delete an AudiobookFile record. Optionally also delete the underlying file from disk.
        /// Disk-delete failures are non-fatal: the DB row is still removed and the failure surfaces
        /// as a warning on the result.
        /// </summary>
        /// <remarks>
        /// This method intentionally does not update the parent audiobook's aggregate
        /// <c>FilePath</c>/<c>FileSize</c> fields. Those are reconciled by the scan flow
        /// (see <c>ScanBackgroundService</c>) and the next scan after a delete will pick up
        /// the change. Callers that need immediate aggregate consistency should update those
        /// fields themselves via <see cref="IAudiobookRepository"/>.
        /// </remarks>
        /// <param name="audiobook">The owning audiobook. The file must belong to this audiobook or the call returns <see cref="DeleteAudiobookFileOutcome.DoesNotBelongToAudiobook"/>.</param>
        /// <param name="fileId">The AudiobookFile row ID to delete.</param>
        /// <param name="deleteFromDisk">When true, also attempt to delete the file from disk.</param>
        /// <param name="source">Optional source identifier for the history entry (e.g., "manual", "smart-move").</param>
        Task<DeleteAudiobookFileResult> DeleteAudiobookFileAsync(Audiobook audiobook, int fileId, bool deleteFromDisk, string? source = "manual", CancellationToken ct = default);
    }
}
