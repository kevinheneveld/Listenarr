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

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Files
{
    public partial class AudiobookFileService
    {
        /// <summary>
        /// Ensure with optional metadata refresh: when the file is already tracked
        /// and <paramref name="forceMetadataRefresh"/> is true, re-extract metadata
        /// and backfill blank library-level audiobook fields.
        /// </summary>
        public async Task<bool> EnsureAudiobookFileAsync(
            Audiobook audiobook,
            string filePath,
            string? source,
            bool forceMetadataRefresh,
            CancellationToken cancellationToken = default)
        {
            var created = await EnsureAudiobookFileAsync(audiobook, filePath, source, cancellationToken);
            if (!created && forceMetadataRefresh && fileSystem.FileExists(filePath))
            {
                await RefreshTrackedFileMetadataAsync(audiobook, filePath, cancellationToken);
            }

            return created;
        }

        public Task RefreshTrackedFileMetadataAsync(
            Audiobook audiobook,
            string filePath,
            CancellationToken cancellationToken = default) =>
            RefreshAudiobookMetadataFromFileAsync(audiobook, filePath);

        public async Task<DeleteAudiobookFileResult> DeleteAudiobookFileAsync(
            Audiobook audiobook,
            int fileId,
            bool deleteFromDisk,
            string? source = "manual",
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(audiobook);

            var file = await audiobookFileRepository.GetByIdAsync(fileId, ct);
            if (file == null)
            {
                return DeleteAudiobookFileResult.NotFound();
            }

            if (file.AudiobookId != audiobook.Id)
            {
                logger.LogWarning(
                    "Refusing to delete AudiobookFile {FileId}: belongs to audiobook {OwnerId}, not {RequestedId}",
                    fileId, file.AudiobookId, audiobook.Id);
                return DeleteAudiobookFileResult.WrongAudiobook(file.Path);
            }

            var warnings = new List<string>();
            var deletedFromDisk = false;

            if (deleteFromDisk && !string.IsNullOrWhiteSpace(file.Path))
            {
                try
                {
                    if (fileSystem.FileExists(file.Path))
                    {
                        fileSystem.DeleteFile(file.Path);
                        deletedFromDisk = true;
                        logger.LogInformation(
                            "Deleted audiobook file from disk for audiobook {AudiobookId}: {Path}",
                            audiobook.Id, LogRedaction.SanitizeFilePath(file.Path));
                    }
                    else
                    {
                        logger.LogInformation(
                            "Skipping disk delete — file not present for audiobook {AudiobookId}: {Path}",
                            audiobook.Id, LogRedaction.SanitizeFilePath(file.Path));
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    warnings.Add($"Could not delete file '{Path.GetFileName(file.Path)}' from disk.");
                    logger.LogWarning(ex, "Failed to delete audiobook file from disk for audiobook {AudiobookId}: {Path}",
                        audiobook.Id, LogRedaction.SanitizeFilePath(file.Path));
                }
            }

            await audiobookFileRepository.DeleteAsync(fileId, ct);
            logger.LogInformation(
                "Deleted AudiobookFile {FileId} for audiobook {AudiobookId}: {Path}",
                fileId, audiobook.Id, LogRedaction.SanitizeFilePath(file.Path));

            try
            {
                var historyEntry = new History
                {
                    AudiobookId = audiobook.Id,
                    AudiobookTitle = audiobook.Title ?? "Unknown",
                    EventType = "File Removed",
                    Message = $"File removed: {Path.GetFileName(file.Path)}",
                    Source = source ?? "manual",
                    Data = JsonSerializer.Serialize(new
                    {
                        FileId = fileId,
                        FilePath = file.Path,
                        FileSize = file.Size,
                        Format = file.Format,
                        DeletedFromDisk = deletedFromDisk,
                        Warnings = warnings
                    }),
                    Timestamp = DateTime.UtcNow
                };
                await historyRepository.AddAsync(historyEntry, ct);
            }
            catch (Exception hx) when (hx is not OperationCanceledException && hx is not OutOfMemoryException && hx is not StackOverflowException)
            {
                logger.LogDebug(hx, "Failed to create history entry for removed audiobook file {Path}", LogRedaction.SanitizeFilePath(file.Path));
            }

            await ClearAgentVerdictIfLastFileRemovedAsync(audiobook, ct);

            return new DeleteAudiobookFileResult
            {
                Outcome = DeleteAudiobookFileOutcome.Deleted,
                DeletedFromDisk = deletedFromDisk,
                Path = file.Path,
                Warnings = warnings
            };
        }

        /// <summary>
        /// A verification verdict describes the audio that was checked. Once the
        /// last tracked file is gone (deleted, purged, reaped), an agent verdict
        /// would keep the book in the review/verified buckets with nothing left
        /// to listen to — clear it back to Unverified. Manual states
        /// (ManuallyVerified, Rejected) are sticky by design and survive: the
        /// wrong-content flow deliberately leaves Rejected on purged records.
        /// Transcript and detail fields are kept as the audit trail of the last
        /// agent pass, mirroring the manual 'clear' action.
        /// </summary>
        private async Task ClearAgentVerdictIfLastFileRemovedAsync(Audiobook audiobook, CancellationToken ct)
        {
            try
            {
                if (audiobook.VerificationStatus is not (VerificationStatus.AgentVerified
                    or VerificationStatus.AgentFlagged
                    or VerificationStatus.AgentUnverifiable))
                {
                    return;
                }

                var remaining = await audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id, ct);
                if (remaining.Count > 0)
                {
                    return;
                }

                audiobook.VerificationStatus = VerificationStatus.Unverified;
                audiobook.VerificationConfidence = null;
                audiobook.VerifiedAt = null;
                audiobook.VerifiedBy = null;
                audiobook.VerificationMethod = null;
                await audiobookRepository.UpdateAsync(audiobook);
                logger.LogInformation(
                    "Cleared agent verification verdict for audiobook {AudiobookId}: last tracked file was removed",
                    audiobook.Id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "Failed to clear agent verdict after last-file removal for audiobook {AudiobookId}", audiobook.Id);
            }
        }
    }
}
