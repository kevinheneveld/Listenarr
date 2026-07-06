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
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Files
{
    // Force-refresh metadata promotion: re-extract an already-tracked file's tags and
    // fill BLANK library-level fields (never overwriting existing values). Split into
    // a partial to keep the main service under the architecture size cap.
    public partial class AudiobookFileService
    {
        /// <summary>
        /// Re-extracts metadata from an already-tracked file and backfills any blank library-level
        /// fields on the parent audiobook record. Used by the force-refresh scan flow to enrich
        /// existing books without touching their AudiobookFile rows. Persists via
        /// <see cref="IAudiobookRepository"/> when something changed.
        /// </summary>
        private async Task RefreshAudiobookMetadataFromFileAsync(Audiobook audiobook, string filePath)
        {
            AudioMetadata? meta = null;
            try
            {
                using var _ = await limiter.Sem.LockAsync();
                meta = await metadataService.ExtractFileMetadataAsync(filePath);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogDebug(ex, "Force-refresh: metadata extraction failed for {Path}", LogRedaction.SanitizeFilePath(filePath));
                return;
            }
            if (meta == null) return;

            var mutated = await PromoteLocalMetadataAsync(audiobook, meta, filePath);
            if (mutated)
            {
                await audiobookRepository.UpdateAsync(audiobook);
            }
        }

        /// <summary>
        /// Fills blank library-level fields on the audiobook record from extracted file metadata,
        /// and extracts an embedded cover into library storage when the audiobook has no image yet.
        /// Never overwrites existing non-blank fields. Audiobook is mutated in place; caller persists.
        /// Returns true if any field was changed (caller can use this to skip a no-op UpdateAsync).
        /// </summary>
        private async Task<bool> PromoteLocalMetadataAsync(Audiobook audiobook, AudioMetadata meta, string filePath)
        {
            var anyChange = PromoteBlankFieldsFromMetadata(audiobook, meta, out var identifiersChanged);

            // Cover extraction is gated on: (a) audiobook has no image yet, (b) ffprobe reported
            // an embedded picture stream. Both keep us from running TagLib for every file in a
            // multi-file book once the cover has been pulled from the first one.
            var hasAttachedPic = meta.AdditionalData != null && meta.AdditionalData.ContainsKey("AttachedPicCodec");
            if (string.IsNullOrWhiteSpace(audiobook.ImageUrl) && hasAttachedPic)
            {
                try
                {
                    var (bytes, ext) = await metadataService.ExtractEmbeddedCoverAsync(filePath);
                    if (bytes != null && bytes.Length > 0)
                    {
                        var identifier = !string.IsNullOrWhiteSpace(audiobook.Asin)
                            ? audiobook.Asin!
                            : "audiobook-" + audiobook.Id;
                        var stored = await imageCache.StoreLibraryImageBytesAsync(identifier, bytes, ext ?? ".jpg");
                        if (!string.IsNullOrWhiteSpace(stored))
                        {
                            audiobook.ImageUrl = "/" + stored;
                            anyChange = true;
                            logger.LogInformation("Promoted embedded cover art to audiobook {AudiobookId} from {File}", audiobook.Id, LogRedaction.SanitizeFilePath(filePath));
                        }
                    }
                }
                catch (Exception coverEx) when (coverEx is not OperationCanceledException && coverEx is not OutOfMemoryException && coverEx is not StackOverflowException)
                {
                    logger.LogDebug(coverEx, "Embedded cover promotion failed for audiobook {AudiobookId} file {File}", audiobook.Id, LogRedaction.SanitizeFilePath(filePath));
                }
            }

            if (identifiersChanged)
            {
                Identifiers.AudiobookIdentifierMapper.SyncImportedIdentifiersFromLegacyFields(audiobook);
            }

            return anyChange;
        }

        /// <summary>
        /// Pure-function promotion of file-tag values into blank Audiobook fields.
        /// Returns true if any field was mutated. Identifier-bearing fields surface separately
        /// via <paramref name="identifiersChanged"/> so callers can re-sync ExternalIdentifiers.
        /// </summary>
        internal static bool PromoteBlankFieldsFromMetadata(Audiobook audiobook, AudioMetadata meta, out bool identifiersChanged)
        {
            identifiersChanged = false;
            if (audiobook == null || meta == null) return false;

            var anyChange = false;

            if (string.IsNullOrWhiteSpace(audiobook.Title) && !string.IsNullOrWhiteSpace(meta.Title))
            { audiobook.Title = meta.Title; anyChange = true; }
            if (string.IsNullOrWhiteSpace(audiobook.Subtitle) && !string.IsNullOrWhiteSpace(meta.Subtitle))
            { audiobook.Subtitle = meta.Subtitle; anyChange = true; }
            if (string.IsNullOrWhiteSpace(audiobook.Series) && !string.IsNullOrWhiteSpace(meta.Series))
            { audiobook.Series = meta.Series; anyChange = true; }
            if (string.IsNullOrWhiteSpace(audiobook.SeriesNumber) && meta.SeriesPosition.HasValue)
            { audiobook.SeriesNumber = meta.SeriesPosition.Value.ToString(System.Globalization.CultureInfo.InvariantCulture); anyChange = true; }
            if (string.IsNullOrWhiteSpace(audiobook.Publisher) && !string.IsNullOrWhiteSpace(meta.Publisher))
            { audiobook.Publisher = meta.Publisher; anyChange = true; }
            if (string.IsNullOrWhiteSpace(audiobook.Language) && !string.IsNullOrWhiteSpace(meta.Language))
            { audiobook.Language = meta.Language; anyChange = true; }
            if (string.IsNullOrWhiteSpace(audiobook.Description) && !string.IsNullOrWhiteSpace(meta.Description))
            { audiobook.Description = meta.Description; anyChange = true; }
            if (string.IsNullOrWhiteSpace(audiobook.PublishYear) && meta.Year.HasValue)
            { audiobook.PublishYear = meta.Year.Value.ToString(System.Globalization.CultureInfo.InvariantCulture); anyChange = true; }

            if (audiobook.Authors == null || audiobook.Authors.Count == 0)
            {
                var candidate = FirstNonEmpty(meta.AlbumArtist, meta.Artist);
                if (!string.IsNullOrWhiteSpace(candidate) &&
                    !(meta.Narrator != null && string.Equals(candidate.Trim(), meta.Narrator.Trim(), StringComparison.OrdinalIgnoreCase)))
                {
                    audiobook.Authors = SplitListTag(candidate);
                    anyChange = true;
                }
            }

            if ((audiobook.Narrators == null || audiobook.Narrators.Count == 0) && !string.IsNullOrWhiteSpace(meta.Narrator))
            {
                audiobook.Narrators = SplitListTag(meta.Narrator);
                anyChange = true;
            }

            if (string.IsNullOrWhiteSpace(audiobook.Asin) && !string.IsNullOrWhiteSpace(meta.Asin) &&
                AudiobookIdentifierNormalizer.TryNormalize(AudiobookExternalIdentifierType.Asin, meta.Asin, out var normalizedAsin, out _))
            {
                audiobook.Asin = normalizedAsin;
                identifiersChanged = true;
                anyChange = true;
            }

            if ((audiobook.Isbn == null || audiobook.Isbn.Count == 0) && !string.IsNullOrWhiteSpace(meta.Isbn) &&
                AudiobookIdentifierNormalizer.TryNormalize(AudiobookExternalIdentifierType.Isbn, meta.Isbn, out var normalizedIsbn, out _))
            {
                audiobook.Isbn = new List<string> { normalizedIsbn };
                identifiersChanged = true;
                anyChange = true;
            }

            return anyChange;
        }

        // Splits only on unambiguous list separators. Commas are excluded because
        // "Last, First"-style single-author tags are common and would split into two authors.
        private static List<string> SplitListTag(string value) =>
            value.Split(new[] { ';', '/' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();

        private static string? FirstNonEmpty(params string?[] candidates)
        {
            foreach (var c in candidates.Where(static c => !string.IsNullOrWhiteSpace(c))) return c;
            return null;
        }
    }
}
