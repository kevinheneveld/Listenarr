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
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Downloads.Import
{
    public partial class DownloadImportService
    {
        /// <summary>
        /// Extract metadata for every audio file up front so the whole release
        /// shape can be inspected before anything is committed (ADR-0001
        /// pre-ingest verification). The returned dictionary doubles as the
        /// cache the per-file import loop reuses, so this costs no extra
        /// ffprobe calls. Returns a rejection reason when the batch looks like
        /// a music album; null when accepted.
        /// </summary>
        private async Task<(Dictionary<string, AudioMetadata?> MetadataByPath, string? RejectionReason)> InspectPreIngestAsync(
            List<string> orderedFiles, ApplicationSettings settings)
        {
            var audioFilePaths = orderedFiles.Where(FileUtils.IsAudioFile).ToList();
            var metadataByPath = new Dictionary<string, AudioMetadata?>(StringComparer.OrdinalIgnoreCase);
            if (settings.EnableMetadataProcessing)
            {
                foreach (var audioFile in audioFilePaths)
                {
                    try
                    {
                        metadataByPath[audioFile] = await metadataService.ExtractFileMetadataAsync(audioFile);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        metadataByPath[audioFile] = null;
                        logger.LogDebug(ex, "ImportFilesFromDirectory: pre-ingest metadata extraction failed for {File}", audioFile);
                    }
                }
            }

            var preIngestMetadata = audioFilePaths
                .Select(p => metadataByPath.TryGetValue(p, out var m) ? m : null)
                .Where(m => m != null)
                .Select(m => m!)
                .ToList();
            var verification = PreIngestVerification.Inspect(preIngestMetadata);
            return (metadataByPath, verification.Rejected ? verification.Reason : null);
        }

        /// <summary>
        /// Run pre-ingest verification over the ordered files. On rejection,
        /// records one Skipped result per file and returns null so the caller
        /// can return early; otherwise returns the extracted metadata cache.
        /// </summary>
        private async Task<Dictionary<string, AudioMetadata?>?> InspectPreIngestOrSkipAllAsync(
            Audiobook audiobook,
            List<string> orderedFiles,
            ApplicationSettings settings,
            List<ImportResult> results)
        {
            var (metadataByPath, preIngestRejection) = await InspectPreIngestAsync(orderedFiles, settings);
            if (preIngestRejection == null)
            {
                return metadataByPath;
            }

            logger.LogWarning(
                "ImportFilesFromDirectory: rejecting completed download for audiobook {AudiobookId} at pre-ingest verification — {Reason}",
                audiobook.Id, preIngestRejection);
            foreach (var _ in orderedFiles)
            {
                results.Add(ImportResult.Skipped($"Pre-ingest verification rejected this download: {preIngestRejection}"));
            }
            return null;
        }

        /// <summary>
        /// Compute the best existing quality among the audiobook's tracked files,
        /// used as the acceptance baseline when scoring incoming candidates.
        /// </summary>
        private static (string? BestExisting, QualityProfile? Profile)
            ResolveExistingQualityBaseline(Audiobook audiobook)
        {
            string? bestExisting = null;
            QualityProfile? abProfile = audiobook.QualityProfile;
            if (audiobook.Files != null && audiobook.Files.Count != 0)
            {
                foreach (var f in audiobook.Files)
                {
                    string q = string.Empty;
                    if (!string.IsNullOrEmpty(f.Format)) q = f.Format;
                    if (f.Bitrate.HasValue)
                    {
                        var kb = f.Bitrate.Value / 1000;
                        if (kb >= 320) q = "MP3 320kbps";
                        else if (kb >= 256) q = "MP3 256kbps";
                        else if (kb >= 192) q = "MP3 192kbps";
                        else if (kb >= 128) q = "MP3 128kbps";
                    }
                    if (string.IsNullOrEmpty(q) && !string.IsNullOrEmpty(f.Path)) q = ImportQualityEvaluator.Determine(null, f.Path);
                    if (string.IsNullOrEmpty(bestExisting)) bestExisting = q;
                    else if (!string.IsNullOrEmpty(q) && !string.IsNullOrEmpty(bestExisting) && abProfile != null && ImportQualityEvaluator.IsAcceptable(q, bestExisting, abProfile)) bestExisting = q;
                }
            }

            return (bestExisting, abProfile);
        }

        /// <summary>
        /// Reuse the pre-ingest extraction; fall back to a direct probe only
        /// when the cache has no entry for this file.
        /// </summary>
        private async Task<AudioMetadata?> ResolveCandidateMetadataAsync(
            string file,
            ApplicationSettings settings,
            Dictionary<string, AudioMetadata?> metadataByPath)
        {
            if (!settings.EnableMetadataProcessing)
            {
                return null;
            }

            return metadataByPath.TryGetValue(file, out var cached)
                ? cached
                : await metadataService.ExtractFileMetadataAsync(file);
        }
    }
}
