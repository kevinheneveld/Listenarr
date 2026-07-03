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

using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    /// <summary>
    /// Read a tracked file's embedded tags via ffprobe — the metadata hint the
    /// relabel ("find correct match") flow ranks candidates with. Read-only.
    /// </summary>
    public sealed class LibraryEmbeddedMetadataWorkflow
    {
        private readonly IAudiobookRepository _repo;
        private readonly IAudiobookFileRepository _audioFileRepository;
        private readonly IFfmpegService _ffmpegService;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger<LibraryEmbeddedMetadataWorkflow> _logger;

        public LibraryEmbeddedMetadataWorkflow(
            IAudiobookRepository repo,
            IAudiobookFileRepository audioFileRepository,
            IFfmpegService ffmpegService,
            IFileSystem fileSystem,
            ILogger<LibraryEmbeddedMetadataWorkflow> logger)
        {
            _repo = repo;
            _audioFileRepository = audioFileRepository;
            _ffmpegService = ffmpegService;
            _fileSystem = fileSystem;
            _logger = logger;
        }

        public async Task<IActionResult> ReadAsync(int audiobookId, int fileId, CancellationToken ct)
        {
            var audiobook = await _repo.GetByIdAsync(audiobookId);
            if (audiobook == null)
            {
                return new NotFoundObjectResult(new { message = "Audiobook not found" });
            }

            var file = await _audioFileRepository.GetByIdAsync(fileId, ct);
            if (file == null || file.AudiobookId != audiobookId)
            {
                return new NotFoundObjectResult(new { message = "File not found." });
            }

            if (string.IsNullOrWhiteSpace(file.Path) || !_fileSystem.FileExists(file.Path))
            {
                return new NotFoundObjectResult(new { message = "File not found." });
            }

            try
            {
                var meta = await _ffmpegService.RunFfprobeAsync(file.Path);
                return new OkObjectResult(new
                {
                    fileId = file.Id,
                    audiobookId,
                    currentPath = file.Path,
                    size = file.Size,
                    title = NullIfEmpty(meta?.Title),
                    author = NullIfEmpty(meta?.Artist),
                    albumArtist = NullIfEmpty(meta?.AlbumArtist),
                    narrator = NullIfEmpty(meta?.Narrator),
                    album = NullIfEmpty(meta?.Album),
                    genre = NullIfEmpty(meta?.Genre),
                    durationSeconds = file.DurationSeconds,
                    bitRate = file.Bitrate,
                    format = file.Format
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "embedded-metadata: ffprobe failed for file {FileId}", fileId);
                return new ObjectResult(new { message = "Could not read embedded metadata" })
                {
                    StatusCode = StatusCodes.Status500InternalServerError
                };
            }
        }

        private static string? NullIfEmpty(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
