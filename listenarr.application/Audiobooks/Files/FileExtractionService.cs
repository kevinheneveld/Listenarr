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
using Listenarr.Application.Downloads.Contracts;
using Listenarr.Domain.Audiobooks.Enumerations;
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Files
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
        private readonly IFileSystem _fileSystem;
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
            IFileSystem fileSystem,
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
            _fileSystem = fileSystem;
            _libraryAddService = libraryAddService;
            _logger = logger;
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

            var sourceFile = await _audiobookFileRepository.GetByIdAsync(fileId, ct);
            if (sourceFile == null || sourceFile.AudiobookId != sourceAudiobookId)
            {
                result.Error = "File does not belong to the source audiobook.";
                return result;
            }

            var sourceAbsolutePath = ResolveAbsolutePath(sourceAudiobook, sourceFile);
            if (string.IsNullOrWhiteSpace(sourceAbsolutePath) || !_fileSystem.FileExists(sourceAbsolutePath))
            {
                result.Error = "Source file not found on disk.";
                return result;
            }

            // Compute the proposed destination folder up front so we can both detect real
            // conflicts (a different audiobook record claiming the same ASIN) and describe
            // the destination on the conflict screen.
            var sourceRootFolder = await ResolveSourceRootFolderAsync(sourceAbsolutePath);
            var proposedDestinationFolder = await ComputeBasePathAsync(sourceRootFolder, request.Metadata);

            // Duplicate detection. Only meaningful when the user has provided an ASIN to
            // match against, and only a real conflict when the matching record is a
            // DIFFERENT audiobook than the source. Source ASIN == chosen ASIN is a non-
            // conflict for extract — the file just needs to move out from under that source
            // and into a new (or specified-existing) destination.
            Audiobook? existingMatch = null;
            if (!string.IsNullOrWhiteSpace(request.Metadata.Asin))
            {
                var shallow = await _audiobookRepository.GetByAsinAsync(request.Metadata.Asin!);
                if (shallow != null && shallow.Id != sourceAudiobookId)
                {
                    existingMatch = shallow;
                }

                if (existingMatch != null && request.DuplicateStrategy == DuplicateStrategy.None)
                {
                    result.Conflict = await BuildConflictAsync(existingMatch, proposedDestinationFolder, ct);
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
                //
                // A duplicate CANNOT carry the contested ASIN: the DB's partial
                // unique index on Audiobooks.Asin rejects the insert outright,
                // and LibraryAddService's race recovery then converts that
                // rejection into "already exists" — which bounced the modal
                // straight back to the conflict step in a loop (live case:
                // every "Make a duplicate" click flashed and returned).
                // BypassDuplicateCheck only ever skipped the app-level dedup;
                // the index is deliberate and non-negotiable. So the duplicate
                // record is created WITHOUT the ASIN — the existing record
                // keeps it, both coexist as a title+author duplicate group,
                // and Settings → Duplicates can settle the pair later.
                if (request.DuplicateStrategy == DuplicateStrategy.Duplicate && existingMatch != null)
                {
                    request.Metadata.Asin = null;
                }

                var addResult = await _libraryAddService.AddToLibraryAsync(new LibraryAddOperationRequest
                {
                    Metadata = request.Metadata,
                    Monitored = request.Monitored,
                    QualityProfileId = request.QualityProfileId,
                    DestinationPath = proposedDestinationFolder,
                    HistorySource = "FileExtraction",
                    HistoryMessage = $"Audiobook created by extracting '{Path.GetFileName(sourceAbsolutePath)}' from '{sourceAudiobook.Title}'.",
                    // User explicitly chose Duplicate; bypass LibraryAddService's own ASIN/ISBN
                    // dedup so we don't bounce them back to the conflict step in a loop.
                    BypassDuplicateCheck = request.DuplicateStrategy == DuplicateStrategy.Duplicate,
                }, ct);

                if (addResult.AlreadyExists)
                {
                    // LibraryAddService deduped against ASIN/ISBN; if user picked Duplicate they
                    // explicitly want a second record, so this is unexpected. Treat as conflict.
                    if (addResult.Audiobook != null)
                    {
                        result.Conflict = await BuildConflictAsync(addResult.Audiobook, proposedDestinationFolder, ct);
                    }
                    result.Error = "Library refused to create a duplicate. Pick Merge instead.";
                    return result;
                }

                if (!addResult.Added || addResult.Audiobook == null)
                {
                    result.Error = string.IsNullOrWhiteSpace(addResult.Message) ? "Failed to create destination audiobook." : addResult.Message;
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
            if (!PathsEqual(sourceAbsolutePath, destinationAbsolutePath) && _fileSystem.FileExists(destinationAbsolutePath))
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
                if (!string.IsNullOrWhiteSpace(targetDir) && !_fileSystem.DirectoryExists(targetDir))
                {
                    _fileSystem.CreateDirectory(targetDir);
                }

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

            // Reassign file row + update summaries. The reassign is a targeted column
            // update (no tracked-graph re-attach — see ReassignAsync); paths are stored
            // absolute, matching how transfer/organize persist them on this codebase.
            try
            {
                await _audiobookFileRepository.ReassignAsync(fileId, destinationAudiobook.Id, destinationAbsolutePath, ct);

                UpdateAudiobookSummary(destinationAudiobook, await _audiobookFileRepository.GetByAudiobookIdAsync(destinationAudiobook.Id, ct));
                await _audiobookRepository.UpdateAsync(destinationAudiobook);

                var remainingSourceFiles = await _audiobookFileRepository.GetByAudiobookIdAsync(sourceAudiobook.Id, ct);
                UpdateAudiobookSummary(sourceAudiobook, remainingSourceFiles);
                await _audiobookRepository.UpdateAsync(sourceAudiobook);

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
            ApplicationSettings settings,
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
                });

                await _historyRepository.AddAsync(new History
                {
                    AudiobookId = destination.Id,
                    AudiobookTitle = destination.Title,
                    EventType = "File Added",
                    Message = $"File extracted from '{source.Title}' (Audiobook #{source.Id}): {Path.GetFileName(newPath)}",
                    Source = "FileExtraction",
                    Timestamp = DateTime.UtcNow,
                });
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
            if (primary.Size is > 0) audiobook.FileSize = primary.Size;
        }

        private async Task<ExtractFileConflict> BuildConflictAsync(Audiobook existing, string? proposedDestinationFolder, CancellationToken ct)
        {
            const int MaxFileSummaries = 20;
            var files = await _audiobookFileRepository.GetByAudiobookIdAsync(existing.Id, ct);
            var fileCount = files.Count;
            var (strategy, reason) = fileCount == 0
                ? ("merge", "The existing audiobook has no files yet — merging would fill in the missing file.")
                : ("duplicate", "The existing audiobook already has files; making a duplicate keeps both copies and lets you choose later.");

            var existingFiles = files
                .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
                .Take(MaxFileSummaries)
                .Select(f => new ExtractFileConflictExistingFile
                {
                    FileId = f.Id,
                    Path = f.Path,
                    Format = f.Format,
                    Size = f.Size is > 0 ? f.Size : null,
                    DurationSeconds = f.DurationSeconds,
                })
                .ToList();

            return new ExtractFileConflict
            {
                ExistingAudiobookId = existing.Id,
                ExistingTitle = existing.Title,
                ExistingAsin = existing.Asin,
                ExistingFileCount = fileCount,
                ExistingBasePath = existing.BasePath,
                ProposedDestinationFolder = proposedDestinationFolder,
                RecommendedStrategy = strategy,
                RecommendationReason = reason,
                ExistingFiles = existingFiles,
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

        private static bool PathsEqual(string? a, string? b)
            => !string.IsNullOrWhiteSpace(a) && !string.IsNullOrWhiteSpace(b)
                && string.Equals(FileUtils.NormalizeStoredPath(a), FileUtils.NormalizeStoredPath(b), StringComparison.OrdinalIgnoreCase);
    }
}
