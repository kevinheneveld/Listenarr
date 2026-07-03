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
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    /// <summary>
    /// Streams a single tracked audio file to the browser so the user can
    /// audition it in place (identify a narrator, check language or quality)
    /// without downloading the whole file. Range processing is enabled so the
    /// native &lt;audio&gt; element can scrub through a large m4b.
    /// </summary>
    public sealed class LibraryFileStreamWorkflow
    {
        private readonly IAudiobookFileRepository _audioFileRepository;
        private readonly IRootFolderRepository _rootFolderRepository;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger<LibraryFileStreamWorkflow> _logger;

        public LibraryFileStreamWorkflow(
            IAudiobookFileRepository audioFileRepository,
            IRootFolderRepository rootFolderRepository,
            IFileSystem fileSystem,
            ILogger<LibraryFileStreamWorkflow> logger)
        {
            _audioFileRepository = audioFileRepository;
            _rootFolderRepository = rootFolderRepository;
            _fileSystem = fileSystem;
            _logger = logger;
        }

        public async Task<IActionResult> StreamAsync(int id, int fileId, CancellationToken ct)
        {
            var file = await _audioFileRepository.GetByIdAsync(fileId, ct);
            if (file == null)
            {
                return new NotFoundObjectResult(new { message = "File not found" });
            }

            // The route-bound audiobook id MUST match the file's audiobook —
            // prevents using a known file id under an arbitrary audiobook URL
            // to probe what's in the library.
            if (file.AudiobookId != id)
            {
                _logger.LogWarning("StreamAudiobookFile: file {FileId} belongs to audiobook {ActualId}, not requested {RequestedId}",
                    fileId, file.AudiobookId, id);
                return new NotFoundObjectResult(new { message = "File not found" });
            }

            if (string.IsNullOrWhiteSpace(file.Path))
            {
                return new NotFoundObjectResult(new { message = "File has no path on disk" });
            }

            string absolutePath;
            try
            {
                absolutePath = Path.GetFullPath(file.Path);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is PathTooLongException || ex is NotSupportedException)
            {
                _logger.LogWarning(ex, "StreamAudiobookFile: rejected malformed path for file {FileId}", fileId);
                return new BadRequestObjectResult(new { message = "Invalid file path" });
            }

            // Defense in depth against path-traversal: the resolved absolute
            // path MUST sit under one of the configured root folders. The DB
            // is the primary source of truth (file rows are populated by
            // Listenarr's own scan code), but if a row's Path were ever
            // tampered with — DB write bug, manual SQL, future endpoint that
            // accepts arbitrary file paths — this is the last line of defense
            // before we hand a PhysicalFile of /etc/shadow to a client.
            var rootFolders = await _rootFolderRepository.GetAllAsync();
            var roots = (rootFolders ?? new List<RootFolder>())
                .Select(r => r.Path)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();
            if (roots.Count == 0 || !roots.Any(root => FileUtils.IsPathInsideOf(absolutePath, root!)))
            {
                _logger.LogWarning("StreamAudiobookFile: rejected file outside configured root folders. fileId={FileId} path={Path}",
                    fileId, LogRedaction.SanitizeFilePath(absolutePath));
                return new NotFoundObjectResult(new { message = "File not accessible" });
            }

            if (!_fileSystem.FileExists(absolutePath))
            {
                return new NotFoundObjectResult(new { message = "File missing on disk" });
            }

            // Reject reparse points / symlinks — same posture as image serving.
            try
            {
                if (_fileSystem.IsReparsePoint(absolutePath))
                {
                    _logger.LogWarning("StreamAudiobookFile: rejected reparse-point file {FileId}", fileId);
                    return new NotFoundObjectResult(new { message = "File not accessible" });
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "StreamAudiobookFile: failed inspecting file attributes for file {FileId}", fileId);
                return new StatusCodeResult(StatusCodes.Status500InternalServerError);
            }

            var contentType = ResolveAudioContentType(absolutePath);
            if (contentType == null)
            {
                _logger.LogInformation("StreamAudiobookFile: unsupported extension for file {FileId} ({Ext})",
                    fileId, Path.GetExtension(absolutePath));
                return new ObjectResult(new { message = "Unsupported audio format for in-browser preview" })
                {
                    StatusCode = StatusCodes.Status415UnsupportedMediaType
                };
            }

            return new PhysicalFileResult(absolutePath, contentType)
            {
                EnableRangeProcessing = true
            };
        }

        // Map common audiobook file extensions to MIME types browsers handle.
        // Returns null for anything we don't want to attempt to stream (the
        // browser would just fail to play it, but better to surface a 415).
        // .aax is intentionally NOT in the list — DRM-protected, the browser
        // can't decode it anyway.
        internal static string? ResolveAudioContentType(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            return ext switch
            {
                ".mp3" => "audio/mpeg",
                ".m4a" => "audio/mp4",
                ".m4b" => "audio/mp4",
                ".mp4" => "audio/mp4",
                ".aac" => "audio/aac",
                ".ogg" => "audio/ogg",
                ".oga" => "audio/ogg",
                ".opus" => "audio/ogg",
                ".flac" => "audio/flac",
                ".wav" => "audio/wav",
                ".webm" => "audio/webm",
                _ => null,
            };
        }
    }
}
