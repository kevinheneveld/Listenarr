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

using System.IO.Compression;
using System.Text.Json;
using Listenarr.Domain.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    /// <summary>
    /// Manual import for files acquired outside the download pipeline (e.g.
    /// DRM-free store purchases): accepts browser uploads targeted at a
    /// specific audiobook, stores them in the book's library folder and
    /// registers AudiobookFile records — the same bookkeeping as a manual
    /// scan, but without requiring filenames to contain the title, because
    /// the user already pointed at the book. Zip archives are accepted and
    /// their audio entries extracted flat (entry leaf names only, so hostile
    /// archive paths cannot escape the destination folder).
    /// </summary>
    public sealed class LibraryUploadWorkflow
    {
        private readonly IAudiobookRepository _repo;
        private readonly IConfigurationService _configurationService;
        private readonly IFileNamingService _fileNamingService;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IFileSystem _fileSystem;
        private readonly ILogger<LibraryUploadWorkflow> _logger;
        private readonly INotificationService? _notificationService;

        public LibraryUploadWorkflow(
            IAudiobookRepository repo,
            IConfigurationService configurationService,
            IFileNamingService fileNamingService,
            IServiceScopeFactory scopeFactory,
            IFileSystem fileSystem,
            ILogger<LibraryUploadWorkflow> logger,
            INotificationService? notificationService = null)
        {
            _repo = repo;
            _configurationService = configurationService;
            _fileNamingService = fileNamingService;
            _scopeFactory = scopeFactory;
            _fileSystem = fileSystem;
            _logger = logger;
            _notificationService = notificationService;
        }

        internal sealed record SkippedUpload(string Name, string Reason);

        public async Task<IActionResult> UploadAsync(int id, IFormFileCollection? files, CancellationToken ct)
        {
            var audiobook = await _repo.GetByIdAsync(id);
            if (audiobook == null) return new NotFoundObjectResult(new { message = "Audiobook not found" });

            if (files == null || files.Count == 0)
            {
                return new BadRequestObjectResult(new { message = "No files were provided" });
            }

            var settings = await _configurationService.GetApplicationSettingsAsync();
            var destDir = audiobook.BasePath;
            if (string.IsNullOrWhiteSpace(destDir))
            {
                if (string.IsNullOrWhiteSpace(settings.OutputPath))
                {
                    return new BadRequestObjectResult(new { message = "Audiobook has no folder yet and no library output path is configured" });
                }

                var namingPattern = !string.IsNullOrWhiteSpace(settings.FolderNamingPattern)
                    ? settings.FolderNamingPattern
                    : settings.FileNamingPattern;
                destDir = LibraryPathPlanner.ComputeAudiobookBaseDirectoryFromPattern(
                    audiobook, settings.OutputPath, namingPattern, _fileNamingService);
            }

            if (!_fileSystem.TryValidateMutationTarget(destDir, [settings.OutputPath, audiobook.BasePath], out var normalizedDest, out var rejectReason))
            {
                return new BadRequestObjectResult(new { message = $"Upload destination rejected: {rejectReason}", path = destDir });
            }

            destDir = normalizedDest;
            _fileSystem.CreateDirectory(destDir);

            var savedPaths = new List<string>();
            var skipped = new List<SkippedUpload>();

            foreach (var file in files)
            {
                ct.ThrowIfCancellationRequested();
                var name = SanitizeFileName(file.FileName ?? string.Empty);
                if (name.Length == 0)
                {
                    skipped.Add(new SkippedUpload(file.FileName ?? "(unnamed)", "Invalid file name"));
                    continue;
                }

                var ext = Path.GetExtension(name);
                try
                {
                    if (string.Equals(ext, ".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        await ExtractZipEntriesAsync(file, name, destDir, savedPaths, skipped, ct);
                    }
                    else if (!FileUtils.AudioExtensions.Contains(ext))
                    {
                        skipped.Add(new SkippedUpload(name, $"Unsupported file type '{ext}'"));
                    }
                    else
                    {
                        await using var source = file.OpenReadStream();
                        await SaveStreamAsync(source, name, file.Length, destDir, savedPaths, skipped, ct);
                    }
                }
                catch (InvalidDataException)
                {
                    skipped.Add(new SkippedUpload(name, "Archive is corrupt or not a zip file"));
                }
                catch (IOException ioEx)
                {
                    _logger.LogWarning(ioEx, "Failed to store uploaded file {Name} for audiobook {AudiobookId}", LogRedaction.SanitizeText(name), audiobook.Id);
                    skipped.Add(new SkippedUpload(name, "Failed to write file to library storage"));
                }
            }

            if (savedPaths.Count == 0)
            {
                // Valid request, nothing new to store (all duplicates or
                // unsupported types) — report why rather than erroring.
                return new OkObjectResult(new { message = "No files imported", uploaded = 0, skipped, audiobook });
            }

            if (string.IsNullOrWhiteSpace(audiobook.BasePath))
            {
                audiobook.BasePath = destDir;
                await _repo.UpdateAsync(audiobook);
            }

            var created = await RegisterSavedFilesAsync(audiobook, savedPaths, ct);
            var updated = await _repo.GetByIdAsync(audiobook.Id);

            await SendAvailableNotificationAsync(audiobook, created.Count, updated);

            _logger.LogInformation(
                "Uploaded {Created} file(s) into '{Title}' ({AudiobookId}) at {Dest} ({Skipped} skipped)",
                created.Count, LogRedaction.SanitizeText(audiobook.Title), audiobook.Id, LogRedaction.SanitizeFilePath(destDir), skipped.Count);

            return new OkObjectResult(new
            {
                message = $"Uploaded {created.Count} file(s)",
                uploaded = created.Count,
                skipped,
                audiobook = updated
            });
        }

        private async Task ExtractZipEntriesAsync(
            IFormFile file,
            string zipName,
            string destDir,
            List<string> savedPaths,
            List<SkippedUpload> skipped,
            CancellationToken ct)
        {
            await using var zipStream = file.OpenReadStream();
            using var archive = new ZipArchive(zipStream, ZipArchiveMode.Read, leaveOpen: true);

            var audioEntries = archive.Entries
                .Where(e => e.Length > 0 && FileUtils.AudioExtensions.Contains(Path.GetExtension(e.Name)))
                .ToList();

            if (audioEntries.Count == 0)
            {
                skipped.Add(new SkippedUpload(zipName, "Zip contains no audio files"));
                return;
            }

            foreach (var entry in audioEntries)
            {
                ct.ThrowIfCancellationRequested();
                // entry.Name is the leaf (FullName carries directories) — using
                // it both flattens the archive and neutralizes zip-slip paths.
                var entryName = SanitizeFileName(entry.Name);
                if (entryName.Length == 0)
                {
                    skipped.Add(new SkippedUpload(entry.FullName, "Invalid entry name"));
                    continue;
                }

                await using var entryStream = entry.Open();
                await SaveStreamAsync(entryStream, entryName, entry.Length, destDir, savedPaths, skipped, ct);
            }
        }

        private async Task SaveStreamAsync(
            Stream source,
            string name,
            long expectedLength,
            string destDir,
            List<string> savedPaths,
            List<SkippedUpload> skipped,
            CancellationToken ct)
        {
            var target = Path.Combine(destDir, name);
            if (_fileSystem.FileExists(target) && expectedLength > 0 && _fileSystem.GetFileLength(target) == expectedLength)
            {
                skipped.Add(new SkippedUpload(name, "A file with this name and size already exists"));
                return;
            }

            target = UniquePath(destDir, name);

            // Uploads can be hundreds of MB — stream straight to disk instead
            // of buffering through memory.
            await using (var output = _fileSystem.OpenWriteStream(target))
            {
                await source.CopyToAsync(output, ct);
            }

            savedPaths.Add(target);
        }

        private string UniquePath(string destDir, string name)
        {
            var candidate = Path.Combine(destDir, name);
            if (!_fileSystem.FileExists(candidate)) return candidate;

            var stem = Path.GetFileNameWithoutExtension(name);
            var ext = Path.GetExtension(name);
            for (var i = 1; ; i++)
            {
                candidate = Path.Combine(destDir, $"{stem} ({i}){ext}");
                if (!_fileSystem.FileExists(candidate)) return candidate;
            }
        }

        internal static string SanitizeFileName(string raw)
        {
            // Take the leaf regardless of separator style: browsers may send
            // "folder/file.mp3" for directory drops, and Path.GetFileName only
            // understands the current OS's separators.
            var leaf = raw.Replace('\\', '/');
            leaf = leaf[(leaf.LastIndexOf('/') + 1)..].Trim();
            if (leaf is "" or "." or "..") return string.Empty;

            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(leaf.Select(c => invalid.Contains(c) ? '_' : c).ToArray()).Trim();
            return cleaned is "" or "." or ".." ? string.Empty : cleaned;
        }

        private async Task<List<AudiobookFile>> RegisterSavedFilesAsync(Audiobook audiobook, List<string> savedPaths, CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var metadataService = scope.ServiceProvider.GetRequiredService<IMetadataService>();
            var audioFileRepository = scope.ServiceProvider.GetRequiredService<IAudiobookFileRepository>();
            var historyRepository = scope.ServiceProvider.GetRequiredService<IHistoryRepository>();

            var basePath = audiobook.BasePath!;
            var existingFiles = await audioFileRepository.GetByAudiobookIdAsync(audiobook.Id);
            var created = new List<AudiobookFile>();

            foreach (var filePath in savedPaths)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var relativePath = Path.GetRelativePath(basePath, filePath);
                    if (existingFiles.Any(f => f.Path == relativePath))
                    {
                        continue;
                    }

                    AudioMetadata? meta = null;
                    try
                    {
                        meta = await metadataService.ExtractFileMetadataAsync(filePath);
                    }
                    catch (Exception mex) when (mex is not OperationCanceledException && mex is not OutOfMemoryException && mex is not StackOverflowException)
                    {
                        _logger.LogWarning(mex, "Failed to extract metadata for uploaded file {File}", LogRedaction.SanitizeFilePath(filePath));
                    }

                    var fileRecord = new AudiobookFile
                    {
                        AudiobookId = audiobook.Id,
                        Path = relativePath,
                        Size = _fileSystem.GetFileLength(filePath),
                        Source = "upload",
                        CreatedAt = DateTime.UtcNow,
                        DurationSeconds = meta?.Duration.TotalSeconds,
                        Format = meta?.Format,
                        Bitrate = meta?.BitRate,
                        SampleRate = meta?.SampleRate,
                        Channels = meta?.Channels
                    };

                    await audioFileRepository.AddAsync(fileRecord);
                    created.Add(fileRecord);

                    await historyRepository.AddAsync(new History
                    {
                        AudiobookId = audiobook.Id,
                        AudiobookTitle = audiobook.Title ?? "Unknown",
                        EventType = "File Added",
                        Message = $"File uploaded: {Path.GetFileName(fileRecord.Path)}",
                        Source = "Upload",
                        Data = JsonSerializer.Serialize(new
                        {
                            FilePath = fileRecord.Path,
                            FileSize = fileRecord.Size,
                            Format = fileRecord.Format,
                            Source = fileRecord.Source
                        }),
                        Timestamp = DateTime.UtcNow
                    });
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to register uploaded file {File}", LogRedaction.SanitizeFilePath(filePath));
                }
            }

            return created;
        }

        private async Task SendAvailableNotificationAsync(Audiobook audiobook, int createdCount, Audiobook? updated)
        {
            if (_notificationService == null || !audiobook.Monitored || createdCount <= 0)
            {
                return;
            }

            try
            {
                var settings = await _configurationService.GetApplicationSettingsAsync();
                var availableData = new
                {
                    id = audiobook.Id,
                    title = audiobook.Title ?? "Unknown Title",
                    authors = audiobook.Authors,
                    asin = audiobook.Asin,
                    imageUrl = audiobook.ImageUrl,
                    description = audiobook.Description,
                    monitored = audiobook.Monitored,
                    qualityProfileId = audiobook.QualityProfileId,
                    filesImported = createdCount,
                    totalFiles = updated?.Files?.Count ?? 0
                };
                await _notificationService.SendNotificationAsync("book-available", availableData, settings.WebhookUrl, settings.EnabledNotificationTriggers);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to send book-available notification for audiobook {AudiobookId}", audiobook.Id);
            }
        }
    }
}
