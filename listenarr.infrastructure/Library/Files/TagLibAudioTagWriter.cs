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

namespace Listenarr.Infrastructure.Library.Files
{
    public class TagLibAudioTagWriter : IAudioTagWriter
    {
        private readonly ILogger<TagLibAudioTagWriter> _logger;

        public TagLibAudioTagWriter(ILogger<TagLibAudioTagWriter> logger)
        {
            _logger = logger;
        }

        public Task WriteAsinTagAsync(string filePath, string asin)
        {
            if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(asin))
                return Task.CompletedTask;

            try
            {
                using var file = TagLib.File.Create(filePath);

                if (file.Tag is TagLib.Mpeg4.AppleTag appleTag)
                    appleTag.SetDashBox("com.apple.iTunes", "ASIN", asin);
                else if (file.GetTag(TagLib.TagTypes.Id3v2) is TagLib.Id3v2.Tag id3Tag)
                {
                    var frame = TagLib.Id3v2.UserTextInformationFrame.Get(id3Tag, "ASIN", true);
                    frame.Text = new[] { asin };
                }
                else if (file.GetTag(TagLib.TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
                    xiph.SetField("ASIN", asin);
                else
                    return Task.CompletedTask;

                file.Save();
                _logger.LogDebug("Wrote ASIN tag '{Asin}' to {File}", asin, LogRedaction.SanitizeFilePath(filePath));
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to write ASIN tag to {File} - import will continue", LogRedaction.SanitizeFilePath(filePath));
            }

            return Task.CompletedTask;
        }

        public Task<(byte[]? Bytes, string? Extension)> ExtractEmbeddedCoverAsync(string filePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                    return Task.FromResult<(byte[]?, string?)>((null, null));

                using var file = TagLib.File.Create(filePath);
                var picture = file.Tag.Pictures?.FirstOrDefault(p => p.Data?.Count > 0);
                if (picture == null) return Task.FromResult<(byte[]?, string?)>((null, null));

                var ext = (picture.MimeType ?? string.Empty).ToLowerInvariant() switch
                {
                    "image/jpeg" or "image/jpg" => ".jpg",
                    "image/png" => ".png",
                    "image/webp" => ".webp",
                    "image/gif" => ".gif",
                    _ => InferExtensionFromMagicBytes(picture.Data.Data) ?? ".jpg"
                };

                return Task.FromResult<(byte[]?, string?)>((picture.Data.Data.ToArray(), ext));
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Could not extract embedded cover from {File}", LogRedaction.SanitizeFilePath(filePath));
                return Task.FromResult<(byte[]?, string?)>((null, null));
            }
        }

        private static string? InferExtensionFromMagicBytes(byte[]? bytes)
        {
            if (bytes == null || bytes.Length < 4) return null;
            if (bytes[0] == 0xFF && bytes[1] == 0xD8) return ".jpg";
            if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47) return ".png";
            if (bytes.Length >= 12 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
                && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50) return ".webp";
            if (bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46) return ".gif";
            return null;
        }
    }
}
