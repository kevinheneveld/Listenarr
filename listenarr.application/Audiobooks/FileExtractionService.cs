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
using Listenarr.Application.Interfaces;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Domain.Common;
using Listenarr.Domain.Models;
using Listenarr.Domain.Models.Enumerations;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks
{
    /// <summary>
    /// Extracts a single tracked file from its current audiobook onto a destination
    /// audiobook constructed from user-supplied Audible metadata. Used when the import
    /// process placed a file under the wrong parent audiobook (e.g. a music album
    /// misfiled under an audiobook record, or two unrelated books grouped as one).
    /// </summary>
    public class FileExtractionService : IFileExtractionService
    {
        private readonly IAudiobookRepository _audiobookRepository;
        private readonly IAudiobookFileRepository _audiobookFileRepository;
        private readonly IHistoryRepository _historyRepository;
        private readonly IRootFolderService _rootFolderService;
        private readonly IConfigurationService _configurationService;
        private readonly IFileNamingService _fileNamingService;
        private readonly IFileMover _fileMover;
        private readonly IMetadataService _metadataService;
        private readonly ILibraryAddService _libraryAddService;
        private readonly ILogger<FileExtractionService> _logger;

        public FileExtractionService(
            IAudiobookRepository audiobookRepository,
            IAudiobookFileRepository audiobookFileRepository,
            IHistoryRepository historyRepository,
            IRootFolderService rootFolderService,
            IConfigurationService configurationService,
            IFileNamingService fileNamingService,
            IFileMover fileMover,
            IMetadataService metadataService,
            ILibraryAddService libraryAddService,
            ILogger<FileExtractionService> logger)
        {
            _audiobookRepository = audiobookRepository;
            _audiobookFileRepository = audiobookFileRepository;
            _historyRepository = historyRepository;
            _rootFolderService = rootFolderService;
            _configurationService = configurationService;
            _fileNamingService = fileNamingService;
            _fileMover = fileMover;
            _metadataService = metadataService;
            _libraryAddService = libraryAddService;
            _logger = logger;
        }

        public async Task<EmbeddedFileMetadata?> ReadEmbeddedAsync(int audiobookId, int fileId, CancellationToken ct = default)
        {
            var audiobook = await _audiobookRepository.GetByIdAsync(audiobookId);
            if (audiobook == null) return null;

            var file = audiobook.Files?.FirstOrDefault(f => f.Id == fileId);
            if (file == null) return null;

            var absolutePath = ResolveAbsolutePath(audiobook, file);
            var meta = !string.IsNullOrWhiteSpace(absolutePath)
                ? await _metadataService.ExtractFileMetadataAsync(absolutePath)
                : null;

            var dto = new EmbeddedFileMetadata
            {
                FileId = file.Id,
                AudiobookId = audiobook.Id,
                CurrentPath = absolutePath,
            };

            if (meta != null)
            {
                dto.Title = NullIfBlank(meta.Title);
                dto.Subtitle = meta.Subtitle;
                dto.Author = NullIfBlank(meta.Artist);
                dto.AlbumArtist = NullIfBlank(meta.AlbumArtist);
                dto.Narrator = meta.Narrator;
                dto.Album = NullIfBlank(meta.Album);
                dto.Description = meta.Description;
                dto.Genre = NullIfBlank(meta.Genre);
                dto.Year = meta.Year;
                dto.Asin = meta.Asin;
                dto.Isbn = meta.Isbn;
                dto.Series = meta.Series;
                dto.SeriesPosition = meta.SeriesPosition;
                dto.DurationSeconds = meta.Duration.TotalSeconds > 0 ? meta.Duration.TotalSeconds : null;
                dto.BitRate = meta.BitRate;
                dto.Format = meta.Format;
            }

            return dto;
        }

        public async Task<ExtractFileResult> ExtractToNewAudiobookAsync(
            int sourceAudiobookId,
            int fileId,
            ExtractFileRequest request,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(request);

            var result = new ExtractFileResult
            {
                SourceAudiobookId = sourceAudiobookId,
                AppliedStrategy = request.DuplicateStrategy,
            };

            var sourceAudiobook = await _audiobookRepository.GetByIdAsync(sourceAudiobookId);
            if (sourceAudiobook == null)
            {
                result.Error = "Source audiobook not found.";
                return result;
            }

            var sourceFile = sourceAudiobook.Files?.FirstOrDefault(f => f.Id == fileId);
            if (sourceFile == null)
            {
                result.Error = "File does not belong to the source audiobook.";
                return result;
            }

            var sourceAbsolutePath = ResolveAbsolutePath(sourceAudiobook, sourceFile);
            if (string.IsNullOrWhiteSpace(sourceAbsolutePath) || !File.Exists(sourceAbsolutePath))
            {
                result.Error = "Source file not found on disk.";
                return result;
            }

            // Duplicate detection — only meaningful when the user has provided an ASIN to match against
            Audiobook? existingMatch = null;
            if (!string.IsNullOrWhiteSpace(request.Metadata.Asin))
            {
                var shallow = await _audiobookRepository.GetByAsinAsync(request.Metadata.Asin!);
                if (shallow != null)
                {
                    // GetByAsinAsync doesn't eager-load Files; reload by id so BuildConflict has
                    // the file count and Merge has the existing file list to number against.
                    existingMatch = await _audiobookRepository.GetByIdAsync(shallow.Id);
                }
                // (Note: if existingMatch.Id == sourceAudiobookId the user is trying to "extract"
                // the file back onto its own audiobook via the same ASIN — pathological but not a
                // crash. It still surfaces as a conflict and the user picks merge to no-op or
                // duplicate to create a second record.)

                if (existingMatch != null && request.DuplicateStrategy == DuplicateStrategy.None)
                {
                    result.Conflict = BuildConflict(existingMatch);
                    result.Error = "An audiobook with this ASIN already exists.";
                    return result;
                }
            }

            // Determine destination audiobook
            Audiobook destinationAudiobook;
            var createdNewAudiobook = false;

            if (request.DuplicateStrategy == DuplicateStrategy.Merge && existingMatch != null)
            {
                destinationAudiobook = existingMatch;
                result.AppliedStrategy = DuplicateStrategy.Merge;
            }
            else
            {
                // Either no existing match, or user explicitly chose Duplicate. Either way, create new.
                var rootFolderPath = await ResolveSourceRootFolderAsync(sourceAbsolutePath);
                var destinationBasePath = await ComputeBasePathAsync(rootFolderPath, request.Metadata);

                var addResult = await _libraryAddService.AddToLibraryAsync(new LibraryAddOperationRequest
                {
                    Metadata = request.Metadata,
                    Monitored = request.Monitored,
                    QualityProfileId = request.QualityProfileId,
                    DestinationPath = destinationBasePath,
                    HistorySource = "FileExtraction",
                    HistoryMessage = $"Audiobook created by extracting '{Path.GetFileName(sourceAbsolutePath)}' from '{sourceAudiobook.Title}'.",
                }, ct);

                if (addResult.AlreadyExists)
                {
                    // LibraryAddService deduped against ASIN/ISBN; if user picked Duplicate they
                    // explicitly want a second record, so this is unexpected. Treat as conflict.
                    if (addResult.Audiobook != null)
                    {
                        result.Conflict = BuildConflict(addResult.Audiobook);
                    }
                    result.Error = "Library refused to create a duplicate. Pick Merge instead.";
                    return result;
                }

                if (!addResult.Added || addResult.Audiobook == null)
                {
                    result.Error = addResult.Message ?? "Failed to create destination audiobook.";
                    return result;
                }

                destinationAudiobook = addResult.Audiobook;
                createdNewAudiobook = true;
                result.AppliedStrategy = request.DuplicateStrategy == DuplicateStrategy.Duplicate
                    ? DuplicateStrategy.Duplicate
                    : DuplicateStrategy.None;
            }

            // Compute destination file path
            var settings = await _configurationService.GetApplicationSettingsAsync();
            var extension = Path.GetExtension(sourceAbsolutePath);
            if (string.IsNullOrWhiteSpace(extension)) extension = ".m4b";

            var existingDestinationFiles = await _audiobookFileRepository.GetByAudiobookIdAsync(destinationAudiobook.Id, ct);
            var willBeMultiFile = existingDestinationFiles.Count >= 1; // joining at least one existing file
            var sequenceNumber = existingDestinationFiles.Count + 1;

            var destinationAbsolutePath = ComputeDestinationFilePath(
                destinationAudiobook,
                request.Metadata,
                settings,
                extension,
                willBeMultiFile,
                sequenceNumber);

            if (string.IsNullOrWhiteSpace(destinationAbsolutePath))
            {
                if (createdNewAudiobook) await TryRemoveAudiobookAsync(destinationAudiobook);
                result.Error = "Could not determine the destination file path.";
                return result;
            }

            // Refuse to overwrite an existing file at the target
            if (!PathsEqual(sourceAbsolutePath, destinationAbsolutePath) && File.Exists(destinationAbsolutePath))
            {
                if (createdNewAudiobook) await TryRemoveAudiobookAsync(destinationAudiobook);
                result.Error = $"Target file already exists: {destinationAbsolutePath}";
                return result;
            }

            // Move on disk — do this BEFORE the DB reassignment so a failed move doesn't leave
            // the file row pointing at a nonexistent path.
            try
            {
                var targetDir = Path.GetDirectoryName(destinationAbsolutePath);
                if (!string.IsNullOrWhiteSpace(targetDir)) Directory.CreateDirectory(targetDir);

                if (!PathsEqual(sourceAbsolutePath, destinationAbsolutePath))
                {
                    var moved = await _fileMover.PerformActionOn(FileAction.Move, sourceAbsolutePath, destinationAbsolutePath);
                    if (!moved)
                    {
                        if (createdNewAudiobook) await TryRemoveAudiobookAsync(destinationAudiobook);
                        result.Error = "Failed to move file on disk.";
                        return result;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Disk move failed during extract for file {FileId}", fileId);
                if (createdNewAudiobook) await TryRemoveAudiobookAsync(destinationAudiobook);
                result.Error = $"Disk move failed: {ex.Message}";
                return result;
            }

            // Reassign file row + update summaries
            try
            {
                sourceFile.AudiobookId = destinationAudiobook.Id;
                sourceFile.Path = StoreRelativeToBase(destinationAudiobook.BasePath, destinationAbsolutePath);
                await _audiobookFileRepository.UpdateAsync(sourceFile, ct);

                UpdateAudiobookSummary(destinationAudiobook, await _audiobookFileRepository.GetByAudiobookIdAsync(destinationAudiobook.Id, ct));
                await _audiobookRepository.UpdateAsync(destinationAudiobook);

                var remainingSourceFiles = await _audiobookFileRepository.GetByAudiobookIdAsync(sourceAudiobook.Id, ct);
                UpdateAudiobookSummary(sourceAudiobook, remainingSourceFiles);
                await _audiobookRepository.UpdateAsync(sourceAudiobook);

                await _audiobookRepository.SaveChangesAsync(ct);

                result.SourceAudiobookEmpty = remainingSourceFiles.Count == 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Database update failed after disk move during extract for file {FileId}; attempting to revert disk move", fileId);
                // Best-effort revert of the disk move so the user can retry
                try
                {
                    if (!PathsEqual(sourceAbsolutePath, destinationAbsolutePath))
                    {
                        await _fileMover.PerformActionOn(FileAction.Move, destinationAbsolutePath, sourceAbsolutePath);
                    }
                }
                catch (Exception revertEx) when (revertEx is not OperationCanceledException && revertEx is not OutOfMemoryException && revertEx is not StackOverflowException)
                {
                    _logger.LogError(revertEx, "Failed to revert disk move after database failure for file {FileId}", fileId);
                }
                if (createdNewAudiobook) await TryRemoveAudiobookAsync(destinationAudiobook);
                result.Error = $"Database update failed: {ex.Message}";
                return result;
            }

            await WriteHistoryEntriesAsync(sourceAudiobook, destinationAudiobook, sourceAbsolutePath, destinationAbsolutePath);

            result.Success = true;
            result.DestinationAudiobookId = destinationAudiobook.Id;
            result.DestinationAudiobookTitle = destinationAudiobook.Title;
            result.NewFilePath = destinationAbsolutePath;
            return result;
        }

        private string ComputeDestinationFilePath(
            Audiobook destinationAudiobook,
            AudibleBookMetadata metadata,
            Domain.Models.Configurations.ApplicationSettings settings,
            string extension,
            bool isMultiFile,
            int sequenceNumber)
        {
            var basePath = destinationAudiobook.BasePath ?? string.Empty;
            var filePattern = isMultiFile ? settings.MultiFileNamingPattern : settings.FileNamingPattern;
            if (string.IsNullOrWhiteSpace(filePattern)) filePattern = "{Title}";

            var fileRelative = _fileNamingService.ApplyNamingPattern(filePattern, metadata, true);
            if (string.IsNullOrWhiteSpace(fileRelative)) fileRelative = SafeFallbackName(metadata);

            var patternHasNumberTokens = filePattern.IndexOf("DiskNumber", StringComparison.OrdinalIgnoreCase) >= 0
                || filePattern.IndexOf("ChapterNumber", StringComparison.OrdinalIgnoreCase) >= 0;

            if (isMultiFile && !patternHasNumberTokens)
            {
                fileRelative = FileUtils.AppendSequenceSuffix(fileRelative, sequenceNumber);
            }

            if (!fileRelative.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            {
                fileRelative += extension;
            }

            return string.IsNullOrWhiteSpace(basePath)
                ? FileUtils.NormalizeStoredPath(fileRelative)
                : FileUtils.NormalizeStoredPath(Path.Combine(basePath, fileRelative));
        }

        private async Task<string> ComputeBasePathAsync(string rootFolderPath, AudibleBookMetadata metadata)
        {
            var settings = await _configurationService.GetApplicationSettingsAsync();
            var folderRelative = _fileNamingService.ApplyNamingPattern(settings.FolderNamingPattern ?? "{Author}/{Title}", metadata, false);
            if (string.IsNullOrWhiteSpace(folderRelative)) folderRelative = SafeFallbackName(metadata);
            return FileUtils.NormalizeStoredPath(Path.Combine(rootFolderPath, folderRelative));
        }

        private async Task<string> ResolveSourceRootFolderAsync(string sourceAbsolutePath)
        {
            var roots = await _rootFolderService.GetAllAsync();
            var match = roots
                .Where(r => !string.IsNullOrWhiteSpace(r.Path)
                    && (PathsEqual(sourceAbsolutePath, r.Path) || FileUtils.IsPathInsideOf(sourceAbsolutePath, r.Path)))
                .OrderByDescending(r => r.Path?.Length ?? 0)
                .FirstOrDefault();
            if (match != null) return match.Path!;

            var fallback = (await _rootFolderService.GetDefaultAsync())?.Path;
            return !string.IsNullOrWhiteSpace(fallback) ? fallback! : Path.GetDirectoryName(sourceAbsolutePath) ?? string.Empty;
        }

        private async Task TryRemoveAudiobookAsync(Audiobook audiobook)
        {
            try
            {
                await _audiobookRepository.DeleteAsync(audiobook);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to roll back created audiobook {Id} after extract failure", audiobook.Id);
            }
        }

        private async Task WriteHistoryEntriesAsync(Audiobook source, Audiobook destination, string previousPath, string newPath)
        {
            try
            {
                await _historyRepository.AddAsync(new History
                {
                    AudiobookId = source.Id,
                    AudiobookTitle = source.Title,
                    EventType = "File Removed",
                    Message = $"File extracted to '{destination.Title}' (Audiobook #{destination.Id}): {Path.GetFileName(previousPath)}",
                    Source = "FileExtraction",
                    Timestamp = DateTime.UtcNow,
                }, default);

                await _historyRepository.AddAsync(new History
                {
                    AudiobookId = destination.Id,
                    AudiobookTitle = destination.Title,
                    EventType = "File Added",
                    Message = $"File extracted from '{source.Title}' (Audiobook #{source.Id}): {Path.GetFileName(newPath)}",
                    Source = "FileExtraction",
                    Timestamp = DateTime.UtcNow,
                }, default);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to write extract history entries for source {SourceId} / destination {DestId}", source.Id, destination.Id);
            }
        }

        private static void UpdateAudiobookSummary(Audiobook audiobook, IReadOnlyList<AudiobookFile> files)
        {
            var withPaths = files.Where(f => !string.IsNullOrWhiteSpace(f.Path)).ToList();
            if (withPaths.Count == 0)
            {
                audiobook.FilePath = null;
                audiobook.FileSize = null;
                return;
            }

            var primary = withPaths.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).First();
            audiobook.FilePath = primary.Path;
            if (primary.Size > 0) audiobook.FileSize = primary.Size;
        }

        private static ExtractFileConflict BuildConflict(Audiobook existing)
        {
            var fileCount = existing.Files?.Count ?? 0;
            var (strategy, reason) = fileCount == 0
                ? ("merge", "The existing audiobook has no files yet — merging would fill in the missing file.")
                : ("duplicate", "The existing audiobook already has files; making a duplicate keeps both copies and lets you choose later.");
            return new ExtractFileConflict
            {
                ExistingAudiobookId = existing.Id,
                ExistingTitle = existing.Title,
                ExistingAsin = existing.Asin,
                ExistingFileCount = fileCount,
                RecommendedStrategy = strategy,
                RecommendationReason = reason,
            };
        }

        private static string SafeFallbackName(AudibleBookMetadata metadata)
        {
            var title = !string.IsNullOrWhiteSpace(metadata.Title) ? metadata.Title!.Trim() : "Untitled";
            return title.Replace('/', '_').Replace('\\', '_');
        }

        private static string ResolveAbsolutePath(Audiobook audiobook, AudiobookFile file)
        {
            var rawPath = file.Path;
            if (string.IsNullOrWhiteSpace(rawPath)) return string.Empty;
            if (Path.IsPathRooted(rawPath)) return FileUtils.NormalizeStoredPath(rawPath);

            var basePath = audiobook.BasePath ?? string.Empty;
            return string.IsNullOrWhiteSpace(basePath)
                ? FileUtils.NormalizeStoredPath(rawPath)
                : FileUtils.NormalizeStoredPath(Path.Combine(basePath, rawPath));
        }

        private static string StoreRelativeToBase(string? basePath, string absolutePath)
        {
            if (string.IsNullOrWhiteSpace(basePath)) return absolutePath;
            try
            {
                var rel = Path.GetRelativePath(basePath, absolutePath);
                return rel.StartsWith("..", StringComparison.Ordinal) ? absolutePath : rel;
            }
            catch
            {
                return absolutePath;
            }
        }

        private static string? NullIfBlank(string? value)
            => string.IsNullOrWhiteSpace(value) ? null : value;

        private static bool PathsEqual(string? a, string? b)
            => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
                && string.Equals(FileUtils.NormalizeStoredPath(a), FileUtils.NormalizeStoredPath(b), StringComparison.OrdinalIgnoreCase);
    }
}
