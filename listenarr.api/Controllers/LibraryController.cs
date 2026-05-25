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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using Listenarr.Domain.Common;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Common;
using Listenarr.Application.Common.Images;
using Listenarr.Domain.Models.Configurations;
using Listenarr.Domain.Models;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Application.Notification;
using Listenarr.Application.Security;
using Listenarr.Application.Metadata;
using Listenarr.Api.Dtos;
using Listenarr.Application.Search;
using Microsoft.AspNetCore.SignalR;
using Listenarr.Application.Audiobooks;
using Listenarr.Api.Attributes;
using Listenarr.Infrastructure.Persistence;

namespace Listenarr.Api.Controllers
{
    [ApiController]
    [Route("api/v{version:apiVersion}/library")]
    [Tags("Library")]
    public class LibraryController : ControllerBase
    {
        private const int MetadataRescanCooldownSeconds = 15;
        private const int MetadataRescanWindowMinutes = 10;
        private const int MetadataRescanMaxRequestsPerWindow = 5;
        private const int MetadataRescanMaxAsinLookupAttempts = 8;
        private const int MetadataRescanMaxIsbnConversionAttempts = 5;
        private static readonly DownloadStatus[] ActiveLibraryDownloadStatuses =
        {
            DownloadStatus.Queued,
            DownloadStatus.Downloading,
            DownloadStatus.Paused,
            DownloadStatus.Processing,
            DownloadStatus.ImportPending
        };
        private readonly IAudiobookRepository _repo;
        private readonly IImageCacheService _imageCacheService;
        private readonly ILogger<LibraryController> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IHistoryRepository _historyRepository;
        private readonly IAudiobookFileRepository _audioFileRepository;
        private readonly IQualityProfileRepository _qualityProfileRepository;
        private readonly IDownloadRepository _downloadRepository;
        private readonly IRootFolderRepository _rootFolderRepository;
        private readonly IScanQueueService? _scanQueueService;
        private readonly IMoveQueueService? _moveQueueService;
        private readonly IFileNamingService _fileNamingService;
        private readonly NotificationService? _notificationService;
        private readonly IRootFolderService? _rootFolderService;
        private readonly ILibraryAddService? _libraryAddService;
        private readonly IRenameService? _renameService;
        private readonly IExternalCoverArtSweepService? _externalCoverArtSweepService;
        private readonly IFileExtractionService? _fileExtractionService;
        /// <summary>Initializes a new instance of <see cref="LibraryController"/>.</summary>
        /// <param name="repo">Repository for audiobook persistence and queries.</param>
        /// <param name="imageCacheService">Service for caching and moving cover images.</param>
        /// <param name="logger">Logger instance for diagnostic messages.</param>
        /// <param name="scopeFactory">Service scope factory used to create scoped services when required.</param>
        /// <param name="historyRepository">Repository for download history records.</param>
        /// <param name="audioFileRepository">Repository for audiobook file records.</param>
        /// <param name="qualityProfileRepository">Repository for quality profile configuration.</param>
        /// <param name="downloadRepository">Repository for active download records.</param>
        /// <param name="rootFolderRepository">Repository for configured root folder paths.</param>
        /// <param name="fileNamingService">Service responsible for applying file naming patterns.</param>
        /// <param name="scanQueueService">Optional background scan queue service for asynchronous scans.</param>
        /// <param name="moveQueueService">Optional background move queue service for processing move requests.</param>
        /// <param name="notificationService">Service for sending webhook notifications.</param>
        /// <param name="rootFolderService">Optional root folder service for managing and enumerating configured root folders used for validating explicit scan paths.</param>
        /// <param name="libraryAddService">Optional shared add-to-library service used by runtime requests and background syncs.</param>
        /// <param name="renameService">Optional organize/rename service used for previewing and executing library file organization.</param>
        /// <param name="externalCoverArtSweepService">Optional sweep service that downloads still-external cover URLs into local library storage on demand.</param>
        /// <param name="fileExtractionService">Optional file-extraction service used to move a single tracked file onto a different (typically new) destination audiobook.</param>
        public LibraryController(
            IAudiobookRepository repo,
            IImageCacheService imageCacheService,
            ILogger<LibraryController> logger,
            IServiceScopeFactory scopeFactory,
            IHistoryRepository historyRepository,
            IAudiobookFileRepository audioFileRepository,
            IQualityProfileRepository qualityProfileRepository,
            IDownloadRepository downloadRepository,
            IRootFolderRepository rootFolderRepository,
            IFileNamingService fileNamingService,
            IScanQueueService? scanQueueService = null,
            IMoveQueueService? moveQueueService = null,
            NotificationService? notificationService = null,
            IRootFolderService? rootFolderService = null,
            ILibraryAddService? libraryAddService = null,
            IRenameService? renameService = null,
            IExternalCoverArtSweepService? externalCoverArtSweepService = null,
            IFileExtractionService? fileExtractionService = null)
        {
            _repo = repo;
            _imageCacheService = imageCacheService;
            _logger = logger;
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _historyRepository = historyRepository;
            _audioFileRepository = audioFileRepository;
            _qualityProfileRepository = qualityProfileRepository;
            _downloadRepository = downloadRepository;
            _rootFolderRepository = rootFolderRepository;
            _fileNamingService = fileNamingService;
            _scanQueueService = scanQueueService;
            _moveQueueService = moveQueueService;
            _notificationService = notificationService;
            _rootFolderService = rootFolderService;
            _libraryAddService = libraryAddService;
            _renameService = renameService;
            _externalCoverArtSweepService = externalCoverArtSweepService;
            _fileExtractionService = fileExtractionService;
        }

        private static bool ComputeWantedFlag(Audiobook audiobook)
        {
            var files = audiobook.Files;
            var hasTrackedFiles = files != null && files.Count > 0;
            return ComputeWantedFlag(audiobook.Monitored, hasTrackedFiles, audiobook.FilePath);
        }

        private static bool ComputeWantedFlag(bool monitored, bool hasTrackedFiles, string? legacyFilePath)
        {
            if (!monitored)
            {
                return false;
            }

            // The library list endpoint should not hit the filesystem for every book.
            // Use AudiobookFiles as the primary source of truth, but honor the legacy
            // primary FilePath during the upgrade window so existing installs do not
            // suddenly flip back to Wanted before file rows are backfilled.
            return !hasTrackedFiles && string.IsNullOrWhiteSpace(legacyFilePath);
        }

        private static string ResolvePathWithOptionalBase(string? basePath, string candidatePath)
        {
            var normalizedPath = candidatePath.Trim();

            if (string.IsNullOrEmpty(normalizedPath))
            {
                return normalizedPath;
            }

            if (Path.IsPathRooted(normalizedPath) || string.IsNullOrWhiteSpace(basePath))
            {
                return normalizedPath;
            }

            var relativePath = normalizedPath.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            // Defensive check: if the candidate path is rooted, do not call Path.Combine
            // because it would discard the base path argument.
            if (Path.IsPathRooted(relativePath))
            {
                return relativePath;
            }

            var normalizedBasePath = basePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.IsNullOrEmpty(normalizedBasePath)
                ? relativePath
                : normalizedBasePath + Path.DirectorySeparatorChar + relativePath;
        }

        public class ScanRequest
        {
            public string? Path { get; set; }

            /// <summary>
            /// If true, re-extract metadata for already-tracked files and backfill blank
            /// library-level fields on the audiobook record (cover, ASIN, ISBN, series, narrator, etc.).
            /// </summary>
            public bool ForceMetadataRefresh { get; set; }
        }

        /// <summary>
        /// Add a new audiobook to the library from search metadata.
        /// </summary>
        /// <param name="request">Audiobook metadata, monitoring preference, quality profile, and optional auto-search flag.</param>
        /// <returns>The newly created audiobook record.</returns>
        [HttpPost("add")]
        public async Task<IActionResult> AddToLibrary([FromBody] AddToLibraryRequest request)
        {
            if (_libraryAddService != null)
            {
                var result = await _libraryAddService.AddToLibraryAsync(new LibraryAddOperationRequest
                {
                    Metadata = request.Metadata,
                    Monitored = request.Monitored,
                    QualityProfileId = request.QualityProfileId,
                    AutoSearch = request.AutoSearch,
                    DestinationPath = request.DestinationPath,
                    SearchResult = request.SearchResult,
                    HistorySource = "AddNew",
                    HistoryMessage = $"Audiobook '{request.Metadata.Title}' added to library from Add New page"
                });

                if (result.AlreadyExists)
                {
                    return Conflict(new { message = result.Message, audiobook = result.Audiobook });
                }

                return Ok(new { message = result.Message, audiobook = result.Audiobook });
            }

            var metadata = request.Metadata;

            _logger.LogInformation("AddToLibrary received metadata: Title={Title}, Asin={Asin}, PublishYear={PublishYear}, Authors={Authors}, Series={Series}",
                LogRedaction.SanitizeText(metadata.Title), LogRedaction.SanitizeText(metadata.Asin), LogRedaction.SanitizeText(metadata.PublishYear),
                LogRedaction.SanitizeText(metadata.Authors != null ? string.Join(", ", metadata.Authors) : "null"),
                LogRedaction.SanitizeText(metadata.Series));

            // If metadata doesn't have PublishYear but we have search result with publishedDate, try to extract year
            if (string.IsNullOrWhiteSpace(metadata.PublishYear) && request.SearchResult != null)
            {
                try
                {
                    if (DateTime.TryParse(request.SearchResult.PublishedDate, out var publishDate))
                    {
                        metadata.PublishYear = publishDate.Year.ToString();
                        _logger.LogInformation("Extracted publish year from search result publishedDate: {Year}", metadata.PublishYear);
                    }
                    else
                    {
                        _logger.LogWarning("Could not parse PublishedDate as DateTime: {PublishedDate}", LogRedaction.SanitizeText(request.SearchResult.PublishedDate));
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to extract publish year from search result publishedDate");
                }
            }

            // Serialize the check-existing + insert critical section per ASIN so
            // concurrent /library/add requests for the same ASIN cannot both
            // pass the dedup check and create duplicate rows. See
            // kevinheneveld/Listenarr#6 for the bug this prevents. The handle
            // is a no-op when ASIN is missing.
            using var asinAddLock = await AudiobookAddLockManager.AcquireAsync(
                metadata.Asin,
                HttpContext.RequestAborted);

            // Check if audiobook already exists in library
            if (!string.IsNullOrEmpty(metadata.Asin))
            {
                var existingByAsin = await _repo.GetByAsinAsync(metadata.Asin);
                if (existingByAsin != null)
                {
                    return Conflict(new { message = "Audiobook already exists in library", audiobook = existingByAsin });
                }
            }

            var firstIsbn = (metadata.Isbn != null && metadata.Isbn.Any()) ? metadata.Isbn.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i)) : null;
            if (!string.IsNullOrWhiteSpace(firstIsbn))
            {
                var existingByIsbn = await _repo.GetByIsbnAsync(firstIsbn);
                if (existingByIsbn != null)
                {
                    return Conflict(new { message = "Audiobook already exists in library", audiobook = existingByIsbn });
                }
            }

            // Move image from temp cache to permanent library storage
            string? imageUrl = metadata.ImageUrl;
            if (!string.IsNullOrEmpty(metadata.Asin))
            {
                try
                {
                    var libraryImagePath = await _imageCacheService.MoveToLibraryStorageAsync(metadata.Asin, metadata.ImageUrl);
                    if (!string.IsNullOrWhiteSpace(libraryImagePath))
                    {
                        imageUrl = $"/{libraryImagePath}";
                        _logger.LogInformation("Moved image for ASIN {Asin} to permanent library storage", LogRedaction.SanitizeText(metadata.Asin));
                    }
                    else
                    {
                        _logger.LogWarning("Failed to move image for ASIN {Asin}, image may not be in temp cache", LogRedaction.SanitizeText(metadata.Asin));
                    }
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Error moving image for ASIN {Asin} to library storage", LogRedaction.SanitizeText(metadata.Asin));
                    // Continue with original image URL if move fails
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.LogWarning(ex, "Error moving image for ASIN {Asin} to library storage", LogRedaction.SanitizeText(metadata.Asin));
                    // Continue with original image URL if move fails
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning(ex, "Error moving image for ASIN {Asin} to library storage", LogRedaction.SanitizeText(metadata.Asin));
                    // Continue with original image URL if move fails
                }
                catch (TaskCanceledException ex)
                {
                    _logger.LogWarning(ex, "Error moving image for ASIN {Asin} to library storage", LogRedaction.SanitizeText(metadata.Asin));
                    // Continue with original image URL if move fails
                }
                catch (InvalidOperationException ex)
                {
                    _logger.LogWarning(ex, "Error moving image for ASIN {Asin} to library storage", LogRedaction.SanitizeText(metadata.Asin));
                    // Continue with original image URL if move fails
                }
                catch (UriFormatException ex)
                {
                    _logger.LogWarning(ex, "Error moving image for ASIN {Asin} to library storage", LogRedaction.SanitizeText(metadata.Asin));
                    // Continue with original image URL if move fails
                }
            }
            else if (metadata.Isbn != null && metadata.Isbn.Any(i => !string.IsNullOrWhiteSpace(i)))
            {
                firstIsbn = metadata.Isbn.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i));
                if (!string.IsNullOrWhiteSpace(firstIsbn))
                {
                    var existingByIsbn = await _repo.GetByIsbnAsync(firstIsbn);
                    if (existingByIsbn != null)
                    {
                        return Conflict(new { message = "Audiobook already exists in library", audiobook = existingByIsbn });
                    }
                }

                try
                {
                    var derivedKey = "img-" + ComputeShortHash(firstIsbn ?? metadata.ImageUrl ?? string.Empty);
                    var libraryImagePath = await _imageCacheService.MoveToLibraryStorageAsync(derivedKey, metadata.ImageUrl);
                    if (!string.IsNullOrWhiteSpace(libraryImagePath))
                    {
                        imageUrl = $"/{libraryImagePath}";
                        _logger.LogInformation("Moved image for derived ISBN {Key} to permanent library storage", LogRedaction.SanitizeText(derivedKey));
                    }
                    else
                    {
                        _logger.LogWarning("Failed to move image for derived ISBN {Key}, image may not be reachable", LogRedaction.SanitizeText(derivedKey));
                    }
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Error moving image for derived ISBN to library storage");
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.LogWarning(ex, "Error moving image for derived ISBN to library storage");
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning(ex, "Error moving image for derived ISBN to library storage");
                }
                catch (TaskCanceledException ex)
                {
                    _logger.LogWarning(ex, "Error moving image for derived ISBN to library storage");
                }
                catch (InvalidOperationException ex)
                {
                    _logger.LogWarning(ex, "Error moving image for derived ISBN to library storage");
                }
                catch (UriFormatException ex)
                {
                    _logger.LogWarning(ex, "Error moving image for derived ISBN to library storage");
                }
            }
            else if (!string.IsNullOrEmpty(metadata.ImageUrl))
            {
                // No ASIN or ISBN available; attempt to move/download the image using a derived key
                try
                {
                    var rawKey = request.SearchResult?.Id ?? request.SearchResult?.ResultUrl ?? request.SearchResult?.ProductUrl ?? metadata.ImageUrl;
                    var derivedKey = "img-" + ComputeShortHash(rawKey);
                    var libraryImagePath = await _imageCacheService.MoveToLibraryStorageAsync(derivedKey, metadata.ImageUrl);
                    if (!string.IsNullOrWhiteSpace(libraryImagePath))
                    {
                        imageUrl = $"/{libraryImagePath}";
                        _logger.LogInformation("Moved image for derived key {Key} to permanent library storage", LogRedaction.SanitizeText(derivedKey));
                    }
                    else
                    {
                        _logger.LogWarning("Failed to move image for derived key {Key}, image may not be reachable", LogRedaction.SanitizeText(derivedKey));
                    }
                }
                catch (IOException ex)
                {
                    _logger.LogWarning(ex, "Error moving image for derived key when ASIN is missing");
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.LogWarning(ex, "Error moving image for derived key when ASIN is missing");
                }
                catch (HttpRequestException ex)
                {
                    _logger.LogWarning(ex, "Error moving image for derived key when ASIN is missing");
                }
                catch (TaskCanceledException ex)
                {
                    _logger.LogWarning(ex, "Error moving image for derived key when ASIN is missing");
                }
                catch (InvalidOperationException ex)
                {
                    _logger.LogWarning(ex, "Error moving image for derived key when ASIN is missing");
                }
                catch (UriFormatException ex)
                {
                    _logger.LogWarning(ex, "Error moving image for derived key when ASIN is missing");
                }
            }

            // Convert metadata to Audiobook entity and save to database
            var audiobook = metadata.ToAudiobook();

            audiobook.Monitored = request.Monitored; // Use custom monitored setting
            audiobook.ImageUrl = imageUrl;

            AudiobookSeriesMembershipHelper.ApplyToAudiobook(
                audiobook,
                metadata.SeriesMemberships,
                metadata.Series,
                AudibleBookMetadata.ToStringOrFirst(metadata.SeriesNumber));

            SyncImportedIdentifiersFromLegacyFields(audiobook);

            _logger.LogInformation("Created Audiobook entity: Title={Title}, Asin={Asin}, PublishYear={PublishYear}",
                LogRedaction.SanitizeText(audiobook.Title), LogRedaction.SanitizeText(audiobook.Asin), LogRedaction.SanitizeText(audiobook.PublishYear));

            // Assign quality profile - use custom if provided, otherwise default
            if (request.QualityProfileId.HasValue)
            {
                audiobook.QualityProfileId = request.QualityProfileId.Value;
                _logger.LogInformation("Assigned custom quality profile ID {ProfileId} to new audiobook '{Title}'",
                    request.QualityProfileId.Value, LogRedaction.SanitizeText(audiobook.Title));
            }
            else
            {
                // Assign default quality profile to new audiobooks
                using (var scope = _scopeFactory.CreateScope())
                {
                    var qualityProfileService = scope.ServiceProvider.GetRequiredService<IQualityProfileService>();
                    var defaultProfile = await qualityProfileService.GetDefaultAsync();
                    if (defaultProfile != null)
                    {
                        audiobook.QualityProfileId = defaultProfile.Id;
                        _logger.LogInformation("Assigned default quality profile '{ProfileName}' (ID: {ProfileId}) to new audiobook '{Title}'",
                            defaultProfile.Name, defaultProfile.Id, audiobook.Title);
                    }
                    else
                    {
                        _logger.LogWarning("No default quality profile found. New audiobook '{Title}' will not have a quality profile assigned.", LogRedaction.SanitizeText(audiobook.Title));
                    }
                }
            }

            // Compute or use custom BasePath (but don't create the directory yet - that happens during import)
            if (!string.IsNullOrWhiteSpace(request.DestinationPath))
            {
                // User provided a custom destination path - store it as BasePath
                // ImportService will recognize BasePath as set and use filename-only pattern
                audiobook.BasePath = FileUtils.NormalizeStoredPath(request.DestinationPath);
                _logger.LogInformation("Using custom destination path for audiobook '{Title}': {BasePath}",
                    audiobook.Title, audiobook.BasePath);
            }
            // If no custom path provided, leave BasePath null
            // ImportService will use the default naming pattern from settings

            await _repo.AddAsync(audiobook);

            // Resolve author ASINs and cache author images via Audible when possible
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var audible = scope.ServiceProvider.GetRequiredService<AudibleService>();

                if (audiobook.Authors != null && audiobook.Authors.Any())
                {
                    audiobook.AuthorAsins = audiobook.AuthorAsins ?? new List<string>();
                    foreach (var authorName in audiobook.Authors)
                    {
                        try
                        {
                            var info = await audible.LookupAuthorAsync(authorName);
                            if (info != null && !string.IsNullOrWhiteSpace(info.Asin))
                            {
                                // Avoid duplicates
                                if (!audiobook.AuthorAsins.Contains(info.Asin))
                                {
                                    audiobook.AuthorAsins.Add(info.Asin);
                                }

                                // Ensure author image is cached in authors folder (will download if necessary)
                                try
                                {
                                    var moved = await _imageCacheService.MoveToAuthorLibraryStorageAsync(info.Asin, info.Image);
                                    if (moved != null)
                                    {
                                        _logger.LogInformation("Cached author image for {Author} (ASIN: {Asin})", authorName, info.Asin);
                                    }
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                                {
                                    _logger.LogWarning(ex, "Failed to cache author image for {Author}", authorName);
                                }
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            _logger.LogWarning(ex, "Author lookup failed for {Author}", authorName);
                        }
                    }

                    // Persist any updated author ASINs
                    try
                    {
                        await _repo.UpdateAsync(audiobook);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Failed to persist author ASINs for audiobook '{Title}'", LogRedaction.SanitizeText(audiobook.Title));
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Error resolving author ASINs for audiobook '{Title}'", LogRedaction.SanitizeText(audiobook.Title));
            }

            // Send notification if configured
            if (_notificationService != null)
            {
                using var scope = _scopeFactory.CreateScope();
                var configService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
                var settings = await configService.GetApplicationSettingsAsync();
                var data = new
                {
                    id = audiobook.Id,
                    title = audiobook.Title ?? "Unknown Title",
                    authors = audiobook.Authors,
                    narrators = audiobook.Narrators,
                    description = audiobook.Description,
                    asin = audiobook.Asin,
                    publisher = audiobook.Publisher,
                    year = audiobook.PublishYear,
                    imageUrl = audiobook.ImageUrl
                };
                await _notificationService.SendNotificationAsync("book-added", data, settings.WebhookUrl, settings.EnabledNotificationTriggers);
            }


            // Directory creation has been deferred to file import time to avoid creating empty directories
            // for audiobooks that may never be downloaded. If a custom destination path was specified when
            // adding the audiobook, it will be stored in BasePath and used when ImportService processes
            // the downloaded files. If no custom path was specified, ImportService will use the configured
            // naming pattern and output path to determine the directory structure.

            // Log history entry for the added audiobook
            var historyEntry = new History
            {
                AudiobookId = audiobook.Id,
                AudiobookTitle = audiobook.Title ?? "Unknown Title",
                EventType = "Added",
                Message = $"Audiobook '{audiobook.Title}' added to library from Add New page",
                Source = "AddNew",
                Timestamp = DateTime.UtcNow
            };

            await _historyRepository.AddAsync(historyEntry);

            _logger.LogInformation("Added audiobook '{Title}' (ASIN: {Asin}) to library with Monitored={Monitored}, QualityProfileId={QualityProfileId}, AutoSearch={AutoSearch}",
                audiobook.Title, audiobook.Asin, request.Monitored, audiobook.QualityProfileId, request.AutoSearch);

            return Ok(new { message = "Audiobook added to library successfully", audiobook });
        }

        /// <summary>
        /// Preview the destination path that would be computed for an audiobook based on current naming settings.
        /// </summary>
        /// <param name="request">Audiobook metadata and optional destination root override.</param>
        /// <returns>Full path, relative path, and root directory.</returns>
        [HttpPost("preview-path")]
        public async Task<IActionResult> PreviewPath([FromBody] PreviewPathRequest request)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var configService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
                var settings = await configService.GetApplicationSettingsAsync();

                var root = !string.IsNullOrEmpty(request.DestinationRoot) ? request.DestinationRoot : settings.OutputPath;

                // Build a temporary Audiobook to feed naming pattern logic
                var temp = request.Metadata.ToAudiobook();

                AudiobookSeriesMembershipHelper.ApplyToAudiobook(
                    temp,
                    request.Metadata.SeriesMemberships,
                    request.Metadata.Series,
                    request.Metadata.SeriesNumber);

                var namingPattern = !string.IsNullOrWhiteSpace(settings.FolderNamingPattern)
                    ? settings.FolderNamingPattern
                    : settings.FileNamingPattern;
                var full = ComputeAudiobookBaseDirectoryFromPattern(temp, root ?? string.Empty, namingPattern);

                var relative = full;
                if (!string.IsNullOrEmpty(root) && full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    relative = full.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                }

                return Ok(new { fullPath = full, relativePath = relative, root = root });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to compute preview path");
                return StatusCode(500, new { message = "Failed to compute preview path" });
            }
        }

        /// <summary>
        /// Get all audiobooks in the library using a slim list payload.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var allAudiobooks = await _repo.GetAllAsync();
            var audiobooks = allAudiobooks
                .OrderBy(a => a.Title)
                .ToList();

            if (audiobooks.Count == 0)
            {
                return Ok(Array.Empty<LibraryAudiobookListItemDto>());
            }

            // Because this endpoint already loads the entire audiobook table, fetch file
            // summaries directly instead of expanding a large in-memory ID list into SQL.
            var allFiles = await _audioFileRepository.GetAllAsync();
            var fileSummaries = allFiles.Select(f => new AudiobookFileStatusInfo
            {
                AudiobookId = f.AudiobookId,
                Path = f.Path,
                Format = f.Format,
                Container = f.Container,
                Codec = f.Codec,
                Bitrate = f.Bitrate
            }).ToList();

            var filesByAudiobookId = fileSummaries
                .GroupBy(f => f.AudiobookId)
                .ToDictionary(g => g.Key, g => (IReadOnlyList<AudiobookFileStatusInfo>)g.ToList());

            var qualityProfileIds = audiobooks
                .Where(a => a.QualityProfileId.HasValue)
                .Select(a => a.QualityProfileId!.Value)
                .Distinct()
                .ToArray();

            var allQualityProfiles = await _qualityProfileRepository.GetAllAsync();
            var qualityProfiles = qualityProfileIds.Length == 0
                ? new List<QualityProfile>()
                : allQualityProfiles.Where(q => qualityProfileIds.Contains(q.Id)).ToList();

            var qualityProfilesById = qualityProfiles.ToDictionary(q => q.Id);

            var activeDownloadAudiobookIds = await _downloadRepository.GetActiveAudiobookIdsAsync(ActiveLibraryDownloadStatuses);

            var activeDownloadAudiobookIdSet = activeDownloadAudiobookIds.ToHashSet();

            var dto = audiobooks.Select(a =>
            {
                filesByAudiobookId.TryGetValue(a.Id, out var files);
                var hasTrackedFiles = files != null && files.Count > 0;
                var hasLegacyFileSummary = !string.IsNullOrWhiteSpace(a.FilePath);
                var hasAnyFile = hasTrackedFiles || hasLegacyFileSummary;
                var wanted = ComputeWantedFlag(a.Monitored, hasTrackedFiles, a.FilePath);
                QualityProfile? qualityProfile = null;
                if (a.QualityProfileId.HasValue)
                {
                    qualityProfilesById.TryGetValue(a.QualityProfileId.Value, out qualityProfile);
                }

                return new LibraryAudiobookListItemDto
                {
                    Id = a.Id,
                    Title = a.Title,
                    Authors = a.Authors?.ToArray(),
                    Narrators = a.Narrators?.ToArray(),
                    PublishYear = a.PublishYear,
                    PublishedDate = a.PublishedDate,
                    Series = a.Series,
                    SeriesNumber = a.SeriesNumber,
                    Genres = a.Genres?.ToArray(),
                    Asin = a.Asin,
                    OpenLibraryId = a.OpenLibraryId,
                    Publisher = a.Publisher,
                    Language = a.Language,
                    Runtime = a.Runtime,
                    Edition = a.Edition,
                    ImageUrl = a.ImageUrl,
                    Monitored = a.Monitored,
                    BasePath = a.BasePath,
                    FilePath = a.FilePath,
                    FileSize = a.FileSize,
                    FileCount = files?.Count ?? 0,
                    Quality = a.Quality,
                    QualityProfileId = a.QualityProfileId,
                    AuthorAsins = a.AuthorAsins?.ToArray(),
                    Wanted = wanted,
                    Status = AudiobookStatusEvaluator.ComputeStatus(
                         activeDownloadAudiobookIdSet.Contains(a.Id),
                         hasAnyFile,
                         a.Quality,
                         qualityProfile,
                         files)
                };
            }).ToList();

            return Ok(dto);
        }

        /// <summary>
        /// Look up an audiobook by its ASIN.
        /// </summary>
        /// <param name="asin">Amazon Standard Identification Number.</param>
        [HttpGet("by-asin/{asin}")]
        public async Task<IActionResult> GetByAsin(string asin)
        {
            var book = await _repo.GetByAsinAsync(asin);
            if (book == null) return NotFound();
            return Ok(book);
        }

        /// <summary>
        /// Look up an audiobook by its ISBN.
        /// </summary>
        /// <param name="isbn">International Standard Book Number.</param>
        [HttpGet("by-isbn/{isbn}")]
        public async Task<IActionResult> GetByIsbn(string isbn)
        {
            var book = await _repo.GetByIsbnAsync(isbn);
            if (book == null) return NotFound();
            return Ok(book);
        }

        /// <summary>
        /// Get a single audiobook by its database ID, including files, external identifiers, and wanted status.
        /// </summary>
        /// <param name="id">Audiobook ID.</param>
        [HttpGet("{id}")]
        public async Task<ActionResult<Audiobook>> GetAudiobook(int id)
        {
            var updated = await _repo.GetByIdAsync(id);

            if (updated == null)
                return NotFound(new { message = "Audiobook not found" });

            var audiobookDto = new
            {
                id = updated.Id,
                title = updated.Title,
                subtitle = updated.Subtitle,
                authors = updated.Authors,
                narrators = updated.Narrators,
                description = updated.Description,
                genres = updated.Genres,
                isbn = updated.Isbn != null ? updated.Isbn.FirstOrDefault() : null,
                isbns = updated.Isbn,
                asin = updated.Asin,
                openLibraryId = updated.OpenLibraryId,
                identifiers = GetEffectiveIdentifiers(updated).Select(ToIdentifierResponse).ToList(),
                imageUrl = updated.ImageUrl,
                publishYear = updated.PublishYear,
                publisher = updated.Publisher,
                language = updated.Language,
                filePath = updated.FilePath,
                fileSize = updated.FileSize,
                basePath = updated.BasePath,
                runtime = updated.Runtime,
                edition = updated.Edition,
                version = updated.Version,
                @explicit = updated.Explicit,
                abridged = updated.Abridged,
                monitored = updated.Monitored,
                quality = updated.Quality,
                qualityProfileId = updated.QualityProfileId,
                authorAsins = updated.AuthorAsins,
                series = updated.Series,
                seriesNumber = updated.SeriesNumber,
                publishedDate = updated.PublishedDate,
                seriesMemberships = updated.SeriesMemberships?
                    .OrderByDescending(m => m.IsPrimary)
                    .ThenBy(m => m.SortOrder)
                    .Select(m => new
                    {
                        id = m.Id,
                        seriesName = m.SeriesName,
                        seriesNumber = m.SeriesNumber,
                        seriesAsin = m.SeriesAsin,
                        isPrimary = m.IsPrimary,
                        sortOrder = m.SortOrder
                    })
                    .ToList(),
                tags = updated.Tags,
                // Sort via the shared AudiobookFileOrdering helper so multi-file books
                // (e.g. a 14-disc rip) appear in human-natural order — "Disc 01..14"
                // rather than the essentially-undefined row-insertion order EF returns.
                // Same helper feeds AudiobookDtoFactory so the two responses can't drift.
                files = AudiobookFileOrdering.InNaturalOrder(updated.Files).Select(f => new
                {
                    id = f.Id,
                    path = f.Path,
                    size = f.Size,
                    durationSeconds = f.DurationSeconds,
                    format = f.Format,
                    container = f.Container,
                    codec = f.Codec,
                    bitrate = f.Bitrate,
                    sampleRate = f.SampleRate,
                    channels = f.Channels,
                    source = f.Source,
                    createdAt = f.CreatedAt
                }).ToList(),
                wanted = ComputeWantedFlag(updated)
            };

            return Ok(audiobookDto);
        }

        /// <summary>
        /// Get all external identifiers (ASIN, ISBN, Goodreads ID, etc.) for an audiobook.
        /// </summary>
        /// <param name="id">Audiobook ID.</param>
        [HttpGet("{id}/identifiers")]
        public async Task<IActionResult> GetAudiobookIdentifiers(int id)
        {
            var audiobook = await _repo.GetByIdAsync(id);

            if (audiobook == null)
            {
                return NotFound(new { message = "Audiobook not found" });
            }

            var identifiers = GetEffectiveIdentifiers(audiobook)
                .Select(ToIdentifierResponse)
                .ToList();

            return Ok(new
            {
                audiobookId = audiobook.Id,
                identifiers
            });
        }

        /// <summary>
        /// Replace all external identifiers for an audiobook in a single operation.
        /// </summary>
        /// <param name="id">Audiobook ID.</param>
        /// <param name="request">New set of identifiers. Existing identifiers will be removed and replaced.</param>
        [HttpPut("{id}/identifiers")]
        public async Task<IActionResult> ReplaceAudiobookIdentifiers(int id, [FromBody] ReplaceAudiobookIdentifiersRequest? request)
        {
            var audiobook = await _repo.GetByIdAsync(id);

            if (audiobook == null)
            {
                return NotFound(new { message = "Audiobook not found" });
            }

            var incoming = request?.Identifiers ?? new List<AudiobookIdentifierWriteItem>();
            if (incoming.Count > 50)
            {
                return BadRequest(new { message = "Too many identifiers. Maximum is 50." });
            }

            var validationErrors = new List<object>();
            var normalized = new List<AudiobookExternalIdentifier>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var primaryCountByType = new Dictionary<AudiobookExternalIdentifierType, int>();
            var now = DateTime.UtcNow;
            var existingServerOwnedSourceKeys = new HashSet<string>(
                (audiobook.ExternalIdentifiers ?? new List<AudiobookExternalIdentifier>())
                    .Where(i =>
                        i.Source != AudiobookExternalIdentifierSource.Manual &&
                        !string.IsNullOrWhiteSpace(i.ValueNormalized))
                    .Select(IdentifierFullSourceKey),
                StringComparer.OrdinalIgnoreCase);

            for (var index = 0; index < incoming.Count; index++)
            {
                var item = incoming[index];
                if (!Enum.IsDefined(typeof(AudiobookExternalIdentifierType), item.Type))
                {
                    validationErrors.Add(new { index, field = "type", error = "Unsupported identifier type." });
                    continue;
                }

                if (!AudiobookIdentifierNormalizer.TryNormalize(item.Type, item.Value, out var normalizedValue, out var error))
                {
                    validationErrors.Add(new { index, field = "value", error = error ?? "Invalid identifier value." });
                    continue;
                }

                var normalizedRegion = item.Type == AudiobookExternalIdentifierType.Asin
                    ? AudiobookIdentifierNormalizer.NormalizeRegion(item.Region)
                    : null;

                var key = $"{item.Type}|{normalizedValue}|{normalizedRegion ?? string.Empty}";
                if (!seen.Add(key))
                {
                    validationErrors.Add(new { index, field = "value", error = "Duplicate identifier." });
                    continue;
                }

                if (item.IsPrimary)
                {
                    primaryCountByType.TryGetValue(item.Type, out var count);
                    primaryCountByType[item.Type] = count + 1;
                }

                var source = item.Source ?? AudiobookExternalIdentifierSource.Manual;
                if (!Enum.IsDefined(typeof(AudiobookExternalIdentifierSource), source))
                {
                    source = AudiobookExternalIdentifierSource.Manual;
                }
                else if (source != AudiobookExternalIdentifierSource.Manual)
                {
                    // Client writes cannot create or spoof Provider/Imported provenance.
                    // Preserve server-owned provenance only for exact existing rows.
                    var requestedKey = IdentifierFullSourceKey(item.Type, normalizedValue, normalizedRegion, source);
                    if (!existingServerOwnedSourceKeys.Contains(requestedKey))
                    {
                        source = AudiobookExternalIdentifierSource.Manual;
                    }
                }

                normalized.Add(new AudiobookExternalIdentifier
                {
                    AudiobookId = audiobook.Id,
                    Type = item.Type,
                    ValueRaw = AudiobookIdentifierNormalizer.NormalizeRawValueForStorage(item.Value),
                    ValueNormalized = normalizedValue,
                    Region = normalizedRegion,
                    IsPrimary = item.IsPrimary,
                    Source = source,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }

            foreach (var kvp in primaryCountByType.Where(kvp => kvp.Value > 1))
            {
                validationErrors.Add(new
                {
                    field = "isPrimary",
                    type = kvp.Key,
                    error = $"Only one primary identifier is allowed for type {kvp.Key}."
                });
            }

            if (validationErrors.Count > 0)
            {
                return BadRequest(new { message = "Identifier validation failed.", errors = validationErrors });
            }

            // Ensure a primary ASIN exists when ASINs are present.
            var asins = normalized.Where(i => i.Type == AudiobookExternalIdentifierType.Asin).ToList();
            if (asins.Count > 0 && !asins.Any(i => i.IsPrimary))
            {
                asins[0].IsPrimary = true;
            }

            var olids = normalized.Where(i => i.Type == AudiobookExternalIdentifierType.OpenLibraryId).ToList();
            if (olids.Count == 1)
            {
                olids[0].IsPrimary = true;
            }

            audiobook.ExternalIdentifiers = normalized;
            SyncLegacyFieldsFromIdentifiers(audiobook);

            await _repo.UpdateWithIdentifierReplaceAsync(audiobook, normalized);

            _logger.LogInformation(
                "Replaced identifiers for audiobook {AudiobookId} ({Title}). Count={Count}",
                audiobook.Id,
                audiobook.Title,
                normalized.Count);

            return Ok(new
            {
                message = "Audiobook identifiers updated successfully",
                audiobook = new
                {
                    id = audiobook.Id,
                    asin = audiobook.Asin,
                    isbn = audiobook.Isbn,
                    openLibraryId = audiobook.OpenLibraryId
                },
                identifiers = OrderIdentifiers(audiobook.ExternalIdentifiers).Select(ToIdentifierResponse).ToList()
            });
        }

        /// <summary>
        /// Re-fetch metadata for an audiobook from upstream sources (Audible / Audnexus) and update the local record.
        /// </summary>
        /// <param name="id">Audiobook ID.</param>
        [HttpPost("{id}/rescan-metadata")]
        public async Task<IActionResult> RescanAudiobookMetadata(int id)
        {
            using var rescanScope = _scopeFactory.CreateScope();
            var metadataService = rescanScope.ServiceProvider.GetService<IAudiobookMetadataService>();
            var metadataConverters = rescanScope.ServiceProvider.GetService<MetadataConverters>();

            if (metadataService == null || metadataConverters == null)
            {
                _logger.LogError(
                    "Metadata rescan services unavailable. MetadataService={HasMetadataService}, MetadataConverters={HasConverters}",
                    metadataService != null,
                    metadataConverters != null);
                return StatusCode(500, new { message = "Metadata rescan services are not available." });
            }

            var audiobook = await _repo.GetByIdAsync(id);

            if (audiobook == null)
            {
                return NotFound(new { message = "Audiobook not found" });
            }

            var memoryCache = rescanScope.ServiceProvider.GetService<IMemoryCache>();
            if (memoryCache != null &&
                !TryConsumeMetadataRescanQuota(memoryCache, HttpContext, audiobook.Id, out var rateLimitMessage, out var retryAfterSeconds))
            {
                try
                {
                    Response.Headers["Retry-After"] = retryAfterSeconds.ToString();
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogDebug(ex, "Failed to set Retry-After header for metadata rescan rate-limit response");
                }

                return StatusCode(StatusCodes.Status429TooManyRequests, new
                {
                    message = rateLimitMessage,
                    retryAfterSeconds
                });
            }

            var effectiveIdentifiers = GetEffectiveIdentifiers(audiobook);
            var asinIdentifiers = effectiveIdentifiers
                .Where(i => i.Type == AudiobookExternalIdentifierType.Asin)
                .OrderByDescending(i => i.IsPrimary)
                .ThenBy(i => i.Source)
                .ThenBy(i => i.ValueNormalized)
                .ToList();

            var isbnIdentifiers = effectiveIdentifiers
                .Where(i => i.Type == AudiobookExternalIdentifierType.Isbn)
                .OrderByDescending(i => i.IsPrimary)
                .ThenBy(i => i.Source)
                .ThenBy(i => i.ValueNormalized)
                .ToList();

            if (!asinIdentifiers.Any() && !isbnIdentifiers.Any())
            {
                return BadRequest(new { message = "No ASIN or ISBN identifiers are available for metadata rescan." });
            }

            var triedAsinKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var triedAsinDebug = new List<object>();
            var triedIsbnDebug = new List<string>();
            var asinLookupAttempts = 0;
            var isbnConversionAttempts = 0;
            var asinLookupAttemptCapHit = false;
            var isbnConversionAttemptCapHit = false;

            AudibleBookResponse? providerMetadata = null;
            string? providerSource = null;
            string? resolvedAsin = null;
            string? resolvedRegion = null;

            async Task<bool> TryMetadataLookupByAsinAsync(string asin, string? preferredRegion, string via)
            {
                if (!AudiobookIdentifierNormalizer.TryNormalize(
                        AudiobookExternalIdentifierType.Asin,
                        asin,
                        out var normalizedAsin,
                        out _))
                {
                    return false;
                }

                foreach (var region in EnumerateMetadataRescanRegions(preferredRegion))
                {
                    var regionValue = string.IsNullOrWhiteSpace(region) ? "us" : region!;
                    var key = $"{normalizedAsin}|{regionValue}";
                    if (!triedAsinKeys.Add(key))
                    {
                        continue;
                    }

                    triedAsinDebug.Add(new { asin = normalizedAsin, region = regionValue, via });

                    if (asinLookupAttempts >= MetadataRescanMaxAsinLookupAttempts)
                    {
                        asinLookupAttemptCapHit = true;
                        return false;
                    }

                    asinLookupAttempts++;

                    object? rawResult;
                    try
                    {
                        rawResult = await metadataService.GetMetadataAsync(normalizedAsin, regionValue, cache: false);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(
                            ex,
                            "Metadata rescan lookup failed for audiobook {AudiobookId} ({Title}) ASIN {Asin} region {Region}",
                            audiobook.Id,
                            audiobook.Title,
                            normalizedAsin,
                            regionValue);
                        continue;
                    }

                    if (!TryExtractMetadataLookupResult(rawResult, out var extractedMetadata, out var extractedSource) ||
                        extractedMetadata == null)
                    {
                        continue;
                    }

                    providerMetadata = extractedMetadata;
                    providerSource = extractedSource;
                    resolvedAsin = string.IsNullOrWhiteSpace(extractedMetadata.Asin) ? normalizedAsin : extractedMetadata.Asin;
                    resolvedRegion = regionValue;
                    return true;
                }

                return false;
            }

            foreach (var asinIdentifier in asinIdentifiers)
            {
                var asinValue = FirstNonEmpty(asinIdentifier.ValueRaw, asinIdentifier.ValueNormalized);
                if (string.IsNullOrWhiteSpace(asinValue)) continue;

                if (await TryMetadataLookupByAsinAsync(asinValue, asinIdentifier.Region, "asin"))
                {
                    break;
                }

                if (asinLookupAttemptCapHit)
                {
                    break;
                }
            }

            if (providerMetadata == null)
            {
                var asinLookupService = rescanScope.ServiceProvider.GetService<IAsinLookupService>();
                if (asinLookupService == null)
                {
                    _logger.LogWarning("IAsinLookupService not available for ISBN fallback during metadata rescan of audiobook {AudiobookId}", audiobook.Id);
                }

                foreach (var isbnIdentifier in isbnIdentifiers)
                {
                    var isbnValue = FirstNonEmpty(isbnIdentifier.ValueNormalized, isbnIdentifier.ValueRaw);
                    if (string.IsNullOrWhiteSpace(isbnValue)) continue;

                    if (!triedIsbnDebug.Contains(isbnValue, StringComparer.OrdinalIgnoreCase))
                    {
                        triedIsbnDebug.Add(isbnValue);
                    }

                    try
                    {
                        if (isbnConversionAttempts >= MetadataRescanMaxIsbnConversionAttempts)
                        {
                            isbnConversionAttemptCapHit = true;
                            break;
                        }

                        if (asinLookupService == null)
                        {
                            continue;
                        }

                        isbnConversionAttempts++;
                        var (success, asinFromIsbn, _) = await asinLookupService.GetAsinFromIsbnAsync(isbnValue);
                        if (!success || string.IsNullOrWhiteSpace(asinFromIsbn))
                        {
                            continue;
                        }

                        if (await TryMetadataLookupByAsinAsync(asinFromIsbn, null, "isbn"))
                        {
                            break;
                        }

                        if (asinLookupAttemptCapHit)
                        {
                            break;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(
                            ex,
                            "Metadata rescan ASIN conversion failed for audiobook {AudiobookId} ISBN {Isbn}",
                            audiobook.Id,
                            isbnValue);
                    }
                }
            }

            if (providerMetadata == null || string.IsNullOrWhiteSpace(resolvedAsin))
            {
                _logger.LogDebug(
                    "Metadata rescan found no metadata for audiobook {AudiobookId}. TriedAsins={TriedAsins}; TriedIsbns={TriedIsbns}; AsinLookups={AsinLookups}/{AsinCap}; IsbnConversions={IsbnConversions}/{IsbnCap}; Capped={Capped}",
                    audiobook.Id,
                    triedAsinDebug,
                    triedIsbnDebug,
                    asinLookupAttempts,
                    MetadataRescanMaxAsinLookupAttempts,
                    isbnConversionAttempts,
                    MetadataRescanMaxIsbnConversionAttempts,
                    asinLookupAttemptCapHit || isbnConversionAttemptCapHit);

                return NotFound(new
                {
                    message = "No metadata found using the available identifiers."
                });
            }

            var convertedMetadata = metadataConverters.ConvertAudibleToMetadata(
                providerMetadata,
                resolvedAsin,
                string.IsNullOrWhiteSpace(providerSource) ? "Audible" : providerSource!);

            var legacyIdentifierFieldsTouched = ApplyMetadataRescanPatch(audiobook, convertedMetadata);

            if (!string.IsNullOrWhiteSpace(convertedMetadata.ImageUrl))
            {
                audiobook.ImageUrl = await MoveMetadataImageToLibraryStorageAsync(audiobook, convertedMetadata.ImageUrl)
                    ?? convertedMetadata.ImageUrl;
            }

            if (legacyIdentifierFieldsTouched)
            {
                SyncImportedIdentifiersFromLegacyFields(audiobook);
            }

            await _repo.UpdateAsync(audiobook);

            _logger.LogInformation(
                "Metadata rescan updated audiobook {AudiobookId} ({Title}) using {Source} ASIN {Asin} region {Region}",
                audiobook.Id,
                audiobook.Title,
                providerSource ?? "unknown",
                resolvedAsin,
                resolvedRegion ?? "us");

            return Ok(new
            {
                message = "Metadata rescanned successfully",
                audiobookId = audiobook.Id,
                source = providerSource,
                asin = resolvedAsin,
                region = resolvedRegion
            });
        }

        // NOTE: Do not perform ad-hoc schema changes at runtime. Use EF Core migrations to modify the database schema.

        /// <summary>
        /// [Debug] Return raw AudiobookFile database rows for an audiobook. Restricted to local/admin callers.
        /// </summary>
        /// <param name="id">Audiobook ID.</param>
        [HttpGet("{id}/files-debug")]
        [LocalOrAdmin]
        public async Task<IActionResult> GetAudiobookFilesDebug(int id)
        {
            var files = await _audioFileRepository.GetByAudiobookIdAsync(id);
            return Ok(files);
        }

        /// <summary>
        /// Stream a single audio file from a library audiobook for browser-side
        /// preview (narrator identification, language check, etc.). Authentication
        /// flows through the existing SessionAuthenticationMiddleware cookie path
        /// so the browser's native &lt;audio&gt; element can play the URL directly
        /// without needing to set custom headers. Range processing is enabled so
        /// the browser can scrub through a large file without downloading it all.
        /// </summary>
        /// <param name="id">Audiobook ID the file must belong to.</param>
        /// <param name="fileId">AudiobookFile row ID.</param>
        [HttpGet("{id}/files/{fileId}/stream")]
        public async Task<IActionResult> StreamAudiobookFile(int id, int fileId)
        {
            var file = await _audioFileRepository.GetByIdAsync(fileId);
            if (file == null)
            {
                return NotFound(new { message = "File not found" });
            }

            // The route-bound audiobook id MUST match the file's audiobook —
            // prevents using a known file id under an arbitrary audiobook URL
            // to probe what's in the library.
            if (file.AudiobookId != id)
            {
                _logger.LogWarning("StreamAudiobookFile: file {FileId} belongs to audiobook {ActualId}, not requested {RequestedId}",
                    fileId, file.AudiobookId, id);
                return NotFound(new { message = "File not found" });
            }

            if (string.IsNullOrWhiteSpace(file.Path))
            {
                return NotFound(new { message = "File has no path on disk" });
            }

            string absolutePath;
            try
            {
                absolutePath = Path.GetFullPath(file.Path);
            }
            catch (Exception ex) when (ex is ArgumentException || ex is PathTooLongException || ex is NotSupportedException)
            {
                _logger.LogWarning(ex, "StreamAudiobookFile: rejected malformed path for file {FileId}", fileId);
                return BadRequest(new { message = "Invalid file path" });
            }

            // Defense in depth against path-traversal: the resolved absolute
            // path MUST sit under one of the configured root folders. The DB
            // is the primary source of truth (file rows are populated by
            // Listenarr's own scan code), but if a row's Path were ever
            // tampered with — DB write bug, manual SQL, future endpoint that
            // accepts arbitrary file paths — this is the last line of defense
            // before we hand a `PhysicalFile` of `/etc/shadow` to a client.
            var rootFolders = await _rootFolderRepository.GetAllAsync();
            var roots = (rootFolders ?? new List<Listenarr.Domain.Models.RootFolder>())
                .Select(r => r.Path)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToList();
            if (roots.Count == 0 || !roots.Any(root => FileUtils.IsPathInsideOf(absolutePath, root!)))
            {
                _logger.LogWarning("StreamAudiobookFile: rejected file outside configured root folders. fileId={FileId} path={Path}",
                    fileId, LogRedaction.SanitizeFilePath(absolutePath));
                return NotFound(new { message = "File not accessible" });
            }

            if (!System.IO.File.Exists(absolutePath))
            {
                return NotFound(new { message = "File missing on disk" });
            }

            // Reject reparse points / symlinks. Same posture as ImagesController.
            try
            {
                var attrs = System.IO.File.GetAttributes(absolutePath);
                if ((attrs & System.IO.FileAttributes.ReparsePoint) != 0)
                {
                    _logger.LogWarning("StreamAudiobookFile: rejected reparse-point file {FileId}", fileId);
                    return NotFound(new { message = "File not accessible" });
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "StreamAudiobookFile: failed inspecting file attributes for file {FileId}", fileId);
                return StatusCode(500);
            }

            var contentType = ResolveAudioContentType(absolutePath);
            if (contentType == null)
            {
                _logger.LogInformation("StreamAudiobookFile: unsupported extension for file {FileId} ({Ext})",
                    fileId, Path.GetExtension(absolutePath));
                return StatusCode(415, new { message = "Unsupported audio format for in-browser preview" });
            }

            return PhysicalFile(absolutePath, contentType, enableRangeProcessing: true);
        }

        // Map common audiobook file extensions to MIME types browsers handle.
        // Returns null for anything we don't want to attempt to stream (the
        // browser would just fail to play it, but better to surface a 415).
        // .aax is intentionally NOT in the list — DRM-protected, the browser
        // can't decode it anyway.
        private static string? ResolveAudioContentType(string path)
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

        /// <summary>
        /// Update an existing audiobook's metadata and settings. Supports partial updates — only non-null fields are applied.
        /// </summary>
        /// <param name="id">Audiobook ID.</param>
        /// <param name="updatedAudiobook">Fields to update (null fields are left unchanged).</param>
        /// <param name="cacheImageLocally">When true and the ImageUrl is being changed to an external http(s) URL, download the cover into local library storage and rewrite the stored URL to the local path. No-op when the URL is unchanged or already local.</param>
        [HttpPut("{id}")]
        public async Task<IActionResult> UpdateAudiobook(int id, [FromBody] Audiobook updatedAudiobook, [FromQuery] bool cacheImageLocally = false)
        {
            var existingAudiobook = await _repo.GetByIdAsync(id);
            if (existingAudiobook == null)
            {
                return NotFound(new { message = "Audiobook not found" });
            }

            var legacyIdentifierFieldsTouched = false;

            // Only update non-null properties to support partial updates
            if (updatedAudiobook.Title != null) existingAudiobook.Title = updatedAudiobook.Title;
            if (updatedAudiobook.Subtitle != null) existingAudiobook.Subtitle = updatedAudiobook.Subtitle;
            if (updatedAudiobook.Authors != null) existingAudiobook.Authors = updatedAudiobook.Authors;
            if (updatedAudiobook.ImageUrl != null) existingAudiobook.ImageUrl = updatedAudiobook.ImageUrl;
            if (updatedAudiobook.PublishYear != null) existingAudiobook.PublishYear = updatedAudiobook.PublishYear;
            if (updatedAudiobook.PublishedDate != null) existingAudiobook.PublishedDate = updatedAudiobook.PublishedDate;
            if (updatedAudiobook.Description != null) existingAudiobook.Description = updatedAudiobook.Description;
            if (updatedAudiobook.Genres != null) existingAudiobook.Genres = updatedAudiobook.Genres;
            if (updatedAudiobook.Tags != null) existingAudiobook.Tags = updatedAudiobook.Tags;
            if (updatedAudiobook.Narrators != null) existingAudiobook.Narrators = updatedAudiobook.Narrators;
            if (updatedAudiobook.Isbn != null)
            {
                existingAudiobook.Isbn = updatedAudiobook.Isbn;
                legacyIdentifierFieldsTouched = true;
            }
            if (updatedAudiobook.Asin != null)
            {
                existingAudiobook.Asin = updatedAudiobook.Asin;
                legacyIdentifierFieldsTouched = true;
            }
            if (updatedAudiobook.OpenLibraryId != null)
            {
                existingAudiobook.OpenLibraryId = updatedAudiobook.OpenLibraryId;
                legacyIdentifierFieldsTouched = true;
            }
            if (updatedAudiobook.Publisher != null) existingAudiobook.Publisher = updatedAudiobook.Publisher;
            if (updatedAudiobook.Language != null) existingAudiobook.Language = updatedAudiobook.Language;
            if (updatedAudiobook.Runtime != null) existingAudiobook.Runtime = updatedAudiobook.Runtime;
            if (updatedAudiobook.Edition != null) existingAudiobook.Edition = updatedAudiobook.Edition;
            if (updatedAudiobook.Version != null) existingAudiobook.Version = updatedAudiobook.Version;

            var seriesMembershipsTouched =
                updatedAudiobook.SeriesMemberships != null ||
                updatedAudiobook.Series != null ||
                updatedAudiobook.SeriesNumber != null;

            if (seriesMembershipsTouched)
            {
                var mergedSeries = updatedAudiobook.Series ?? existingAudiobook.Series;
                var mergedSeriesNumber = updatedAudiobook.SeriesNumber ?? existingAudiobook.SeriesNumber;
                var existingPrimaryMembership = AudiobookSeriesMembershipHelper.GetPrimaryMembership(existingAudiobook.SeriesMemberships);

                var normalizedMemberships = AudiobookSeriesMembershipHelper.Normalize(
                    updatedAudiobook.SeriesMemberships,
                    mergedSeries,
                    mergedSeriesNumber,
                    existingPrimaryMembership?.SeriesAsin);

                if (existingAudiobook.SeriesMemberships == null)
                {
                    existingAudiobook.SeriesMemberships = new List<AudiobookSeriesMembership>();
                }
                else
                {
                    existingAudiobook.SeriesMemberships.Clear();
                }

                foreach (var membership in normalizedMemberships)
                {
                    existingAudiobook.SeriesMemberships.Add(membership);
                }

                AudiobookSeriesMembershipHelper.ApplyPrimarySeriesFields(existingAudiobook);
            }

            // Always update these fields as they have default values
            existingAudiobook.Explicit = updatedAudiobook.Explicit;
            existingAudiobook.Abridged = updatedAudiobook.Abridged;
            existingAudiobook.Monitored = updatedAudiobook.Monitored;

            if (updatedAudiobook.FilePath != null) existingAudiobook.FilePath = updatedAudiobook.FilePath;
            if (updatedAudiobook.FileSize.HasValue) existingAudiobook.FileSize = updatedAudiobook.FileSize;
            if (updatedAudiobook.Quality != null) existingAudiobook.Quality = updatedAudiobook.Quality;

            // Handle QualityProfileId - if -1 is sent, use default profile
            if (updatedAudiobook.QualityProfileId.HasValue)
            {
                if (updatedAudiobook.QualityProfileId.Value == -1)
                {
                    // -1 means "use default profile"
                    using (var scope = _scopeFactory.CreateScope())
                    {
                        var qualityProfileService = scope.ServiceProvider.GetRequiredService<IQualityProfileService>();
                        var defaultProfile = await qualityProfileService.GetDefaultAsync();
                        if (defaultProfile != null)
                        {
                            existingAudiobook.QualityProfileId = defaultProfile.Id;
                            _logger.LogInformation("Assigned default quality profile '{ProfileName}' (ID: {ProfileId}) to audiobook '{Title}'",
                                defaultProfile.Name, defaultProfile.Id, existingAudiobook.Title);
                        }
                        else
                        {
                            _logger.LogWarning("No default quality profile found. Audiobook '{Title}' quality profile set to null.", LogRedaction.SanitizeText(existingAudiobook.Title));
                            existingAudiobook.QualityProfileId = null;
                        }
                    }
                }
                else
                {
                    existingAudiobook.QualityProfileId = updatedAudiobook.QualityProfileId.Value;
                    _logger.LogInformation("Updated quality profile for audiobook '{Title}' to ID {ProfileId}",
                        existingAudiobook.Title, updatedAudiobook.QualityProfileId.Value);
                }
            }

            // Allow updating BasePath (destination) from the frontend when provided
            if (updatedAudiobook.BasePath != null)
            {
                existingAudiobook.BasePath = FileUtils.NormalizeStoredPath(updatedAudiobook.BasePath);
                _logger.LogInformation("Updated BasePath for audiobook '{Title}' to: {BasePath}", LogRedaction.SanitizeText(existingAudiobook.Title), LogRedaction.SanitizeFilePath(updatedAudiobook.BasePath));
            }

            if (legacyIdentifierFieldsTouched)
            {
                SyncImportedIdentifiersFromLegacyFields(existingAudiobook);
            }

            // Optional: download external cover art into local library storage
            // when the caller asks for it (FE checkbox / backfill modal default).
            // Runs *after* every other merge so the cache key sees the post-merge
            // ASIN — important when ImageUrl and Asin change in the same PUT
            // (the backfill modal does exactly that), otherwise we'd key the
            // cache file on the old ASIN.
            //
            // No "URL changed" guard: the user's intent to cache is encoded in
            // `cacheImageLocally`, not in whether the URL is different. The FE
            // self-limits re-downloads — once the post-cache URL is local, the
            // "Cache cover art locally on save" checkbox auto-hides and the
            // flag stops being sent on subsequent edits. We do still require
            // that `updatedAudiobook.ImageUrl` was present in the request so
            // we don't surprise-cache when the caller omitted the field
            // (e.g., the backfill modal applying only non-cover fields).
            if (cacheImageLocally
                && updatedAudiobook.ImageUrl != null
                && IsExternalHttpImageUrl(existingAudiobook.ImageUrl))
            {
                var externalUrl = existingAudiobook.ImageUrl!;
                var moved = await MoveMetadataImageToLibraryStorageAsync(existingAudiobook, externalUrl);
                // Mirror the add-path's fallback: keep the external URL when the
                // download fails so the user still sees a cover.
                existingAudiobook.ImageUrl = moved ?? externalUrl;
                if (!string.IsNullOrWhiteSpace(moved))
                {
                    _logger.LogInformation("Cached external cover art locally for audiobook {AudiobookId} (key={Key})",
                        existingAudiobook.Id,
                        LogRedaction.SanitizeText(existingAudiobook.Asin ?? "<no-asin>"));
                }
            }

            await _repo.UpdateAsync(existingAudiobook);

            _logger.LogInformation("Updated audiobook '{Title}' (ID: {Id})", LogRedaction.SanitizeText(existingAudiobook.Title), id);

            return Ok(new { message = "Audiobook updated successfully", audiobook = existingAudiobook });
        }

        // Thin alias kept for readability at the call sites in this controller.
        // Predicate logic lives in LibraryImageStorageHelper so the sweep service
        // sees the exact same definition of "external".
        private static bool IsExternalHttpImageUrl(string? url)
            => LibraryImageStorageHelper.IsExternalHttpImageUrl(url);

        /// <summary>
        /// Delete an audiobook from the library, including its cached cover image.
        /// </summary>
        /// <param name="id">Audiobook ID.</param>
        /// <param name="deleteFiles">When true, delete all files within the audiobook folder when it can be done safely; otherwise fall back to tracked audiobook files before removing the library record.</param>
        /// <param name="deleteFolder">When true, also delete the audiobook folder when it can be done safely.</param>
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteAudiobook(int id, [FromQuery] bool deleteFiles = false, [FromQuery] bool deleteFolder = false)
        {
            var audiobook = await _repo.GetByIdAsync(id);
            if (audiobook == null)
            {
                return NotFound(new { message = "Audiobook not found" });
            }

            deleteFiles = deleteFiles || deleteFolder;

            DeleteFilesystemResult? filesystemResult = null;
            if (deleteFiles)
            {
                filesystemResult = await DeleteAudiobookFilesystemAsync(audiobook, deleteFolder);
            }

            // Delete associated image from cache if it exists
            try
            {
                // Prefer ASIN-based cleanup when available
                if (!string.IsNullOrEmpty(audiobook.Asin))
                {
                    var imagePath = await _imageCacheService.GetCachedImagePathAsync(audiobook.Asin);
                    if (imagePath != null)
                    {
                        var fullPath = ResolvePathWithOptionalBase(Directory.GetCurrentDirectory(), imagePath);
                        if (System.IO.File.Exists(fullPath))
                        {
                            System.IO.File.Delete(fullPath);
                            _logger.LogInformation("Deleted cached image for ASIN {Asin}", LogRedaction.SanitizeText(audiobook.Asin));
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(audiobook.ImageUrl))
                {
                    // If ImageUrl points to our cached library folder, extract the filename and delete it
                    try
                    {
                        // Safely extract identifier from an internal library image URL
                        const string __marker = "/config/cache/images/library/";
                        var __url = audiobook.ImageUrl;
                        var __idx = __url.IndexOf(__marker, StringComparison.OrdinalIgnoreCase);
                        if (__idx >= 0)
                        {
                            var filename = __url.Substring(__idx + __marker.Length);
                            // Ensure we only take the file name portion (prevent embedded paths)
                            filename = System.IO.Path.GetFileName(filename);
                            var identifier = System.IO.Path.GetFileNameWithoutExtension(filename);

                            // Validate identifier to a conservative whitelist (alnum, dash, underscore, dot)
                            if (!string.IsNullOrEmpty(identifier) && System.Text.RegularExpressions.Regex.IsMatch(identifier, "^[A-Za-z0-9_\\-\\.]{1,128}$"))
                            {
                                var imagePath = await _imageCacheService.GetCachedImagePathAsync(identifier);
                                if (!string.IsNullOrEmpty(imagePath))
                                {
                                    var fullPath = ResolvePathWithOptionalBase(Directory.GetCurrentDirectory(), imagePath);
                                    if (System.IO.File.Exists(fullPath))
                                    {
                                        System.IO.File.Delete(fullPath);
                                        _logger.LogInformation("Deleted cached image for identifier (from ImageUrl): {Identifier}", LogRedaction.SanitizeText(identifier));
                                    }
                                }
                            }
                            else
                            {
                                _logger.LogWarning("Image identifier from ImageUrl for audiobook id {Id} is invalid: {Identifier}", audiobook.Id, LogRedaction.SanitizeText(identifier));
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Failed to delete cached image based on stored ImageUrl for audiobook id {Id}", audiobook.Id);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to delete cached image for audiobook id {Id}", audiobook.Id);
                // Continue with deletion even if image cleanup fails
            }

            var deleted = await _repo.DeleteByIdAsync(id);
            if (deleted)
            {
                var message = BuildDeleteMessage(filesystemResult);
                return Ok(new
                {
                    message,
                    id,
                    deletedFiles = filesystemResult?.DeletedFiles ?? 0,
                    deletedFolder = filesystemResult?.DeletedFolder,
                    deletedParentFolder = filesystemResult?.DeletedParentFolder,
                    warnings = filesystemResult?.Warnings ?? new List<string>()
                });
            }

            return StatusCode(500, new { message = "Failed to delete audiobook" });
        }

        /// <summary>
        /// Delete a single tracked file from an audiobook. Optionally also remove the file from disk.
        /// </summary>
        /// <param name="id">Owning audiobook ID.</param>
        /// <param name="fileId">AudiobookFile row ID to delete.</param>
        /// <param name="deleteFromDisk">When true, attempt to delete the file from disk too. Disk-delete failures surface as warnings; the DB row is still removed.</param>
        [HttpDelete("{id}/files/{fileId}")]
        public async Task<IActionResult> DeleteAudiobookFile(int id, int fileId, [FromQuery] bool deleteFromDisk = false)
        {
            var ct = HttpContext.RequestAborted;
            var audiobook = await _repo.GetByIdAsync(id);
            if (audiobook == null)
            {
                return NotFound(new { message = "Audiobook not found" });
            }

            DeleteAudiobookFileResult result;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var audioFileService = scope.ServiceProvider.GetRequiredService<IAudiobookFileService>();
                result = await audioFileService.DeleteAudiobookFileAsync(audiobook, fileId, deleteFromDisk, source: "manual", ct: ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Failed to delete AudiobookFile {FileId} for audiobook {AudiobookId}", fileId, id);
                return StatusCode(500, new { message = "Failed to delete file" });
            }

            switch (result.Outcome)
            {
                case DeleteAudiobookFileOutcome.NotFound:
                    return NotFound(new { message = "File not found" });

                case DeleteAudiobookFileOutcome.DoesNotBelongToAudiobook:
                    return BadRequest(new { message = "File does not belong to this audiobook" });

                case DeleteAudiobookFileOutcome.Deleted:
                    return Ok(new
                    {
                        message = "File removed",
                        fileId,
                        deletedFromDisk = result.DeletedFromDisk,
                        path = result.Path,
                        warnings = result.Warnings
                    });

                default:
                    return StatusCode(500, new { message = "Unknown delete outcome" });
            }
        }

        private sealed class DeleteFilesystemResult
        {
            public int DeletedFiles { get; set; }
            public bool DeletedFolder { get; set; }
            public bool DeletedParentFolder { get; set; }
            public List<string> Warnings { get; } = new List<string>();
        }

        private sealed class DeleteFolderTarget
        {
            public required string FolderPath { get; init; }
            public required IReadOnlyCollection<string> ProtectedRoots { get; init; }
        }

        private async Task<DeleteFilesystemResult> DeleteAudiobookFilesystemAsync(Audiobook audiobook, bool deleteFolder)
        {
            var result = new DeleteFilesystemResult();
            var trackedFilePaths = CollectTrackedFilePaths(audiobook);
            var deleteTarget = await ResolveDeleteFolderTargetAsync(audiobook, trackedFilePaths, result);

            if (deleteTarget != null)
            {
                TryDeleteFolderContents(deleteTarget.FolderPath, result);

                if (deleteFolder)
                {
                    await TryDeleteAudiobookFolderAsync(audiobook, deleteTarget, result);
                }
            }
            else
            {
                foreach (var trackedFilePath in trackedFilePaths)
                {
                    TryDeleteFile(trackedFilePath, result);
                }
            }

            return result;
        }

        private static IReadOnlyList<string> CollectTrackedFilePaths(Audiobook audiobook)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(audiobook.FilePath))
            {
                var normalizedLegacy = NormalizePath(audiobook.FilePath);
                if (!string.IsNullOrWhiteSpace(normalizedLegacy))
                {
                    paths.Add(normalizedLegacy);
                }
            }

            if (audiobook.Files != null)
            {
                foreach (var normalizedTracked in audiobook.Files
                    .Select(file => NormalizePath(file.Path))
                    .Where(normalizedTracked => !string.IsNullOrWhiteSpace(normalizedTracked)))
                {
                    paths.Add(normalizedTracked!);
                }
            }

            return paths.ToList();
        }

        private void TryDeleteFile(string path, DeleteFilesystemResult result)
        {
            try
            {
                if (!System.IO.File.Exists(path))
                {
                    return;
                }

                System.IO.File.Delete(path);
                result.DeletedFiles++;
                _logger.LogInformation("Deleted audiobook file {Path}", LogRedaction.SanitizeFilePath(path));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                var warning = $"Could not delete file '{Path.GetFileName(path)}'.";
                result.Warnings.Add(warning);
                _logger.LogWarning(ex, "Failed to delete audiobook file {Path}", LogRedaction.SanitizeFilePath(path));
            }
        }

        private void TryDeleteFolderContents(string folderPath, DeleteFilesystemResult result)
        {
            if (!Directory.Exists(folderPath))
            {
                return;
            }

            string[] files;
            try
            {
                files = Directory.GetFiles(folderPath, "*", SearchOption.AllDirectories);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                result.Warnings.Add("Could not enumerate the audiobook folder contents for deletion.");
                _logger.LogWarning(ex, "Failed to enumerate audiobook folder contents for {FolderPath}", LogRedaction.SanitizeFilePath(folderPath));
                return;
            }

            foreach (var filePath in files)
            {
                TryDeleteFile(filePath, result);
            }

            string[] directories;
            try
            {
                directories = Directory.GetDirectories(folderPath, "*", SearchOption.AllDirectories)
                    .OrderByDescending(path => path.Length)
                    .ToArray();
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                result.Warnings.Add("Some nested folders could not be cleaned up after file deletion.");
                _logger.LogWarning(ex, "Failed to enumerate nested audiobook directories for {FolderPath}", LogRedaction.SanitizeFilePath(folderPath));
                return;
            }

            foreach (var directoryPath in directories)
            {
                try
                {
                    if (!Directory.Exists(directoryPath))
                    {
                        continue;
                    }

                    if (!Directory.EnumerateFileSystemEntries(directoryPath).Any())
                    {
                        Directory.Delete(directoryPath, recursive: false);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    _logger.LogDebug(ex, "Failed to remove nested audiobook directory {FolderPath}", LogRedaction.SanitizeFilePath(directoryPath));
                }
            }
        }

        private async Task<DeleteFolderTarget?> ResolveDeleteFolderTargetAsync(
            Audiobook audiobook,
            IReadOnlyList<string> trackedFilePaths,
            DeleteFilesystemResult result)
        {
            var protectedRoots = await GetProtectedRootPathsAsync();
            var folderPath = ResolveAudiobookFolderPath(audiobook, trackedFilePaths);
            if (string.IsNullOrWhiteSpace(folderPath))
            {
                result.Warnings.Add("Audiobook folder could not be determined, so only tracked audiobook files were deleted.");
                return null;
            }

            if (protectedRoots.Any(root => PathsEqual(root, folderPath)))
            {
                var fallbackFolderPath = ResolveTrackedFolderPath(trackedFilePaths);
                if (!string.IsNullOrWhiteSpace(fallbackFolderPath)
                    && !protectedRoots.Any(root => PathsEqual(root, fallbackFolderPath))
                    && IsSamePathOrWithin(fallbackFolderPath, folderPath))
                {
                    folderPath = fallbackFolderPath;
                }
            }

            if (IsFilesystemRoot(folderPath))
            {
                result.Warnings.Add("Refused to delete all files in a filesystem root folder.");
                return null;
            }

            if (protectedRoots.Any(root => PathsEqual(root, folderPath)))
            {
                result.Warnings.Add("Refused to delete all files in a configured library root folder.");
                return null;
            }

            if (!Directory.Exists(folderPath))
            {
                return null;
            }

            var allFiles = await _audioFileRepository.GetAllAsync();
            var otherFilePaths = allFiles
                .Where(f => f.AudiobookId != audiobook.Id && f.Path != null)
                .Select(f => f.Path!)
                .ToList();

            if (otherFilePaths
                .Select(NormalizePath)
                .Any(p => !string.IsNullOrWhiteSpace(p) && IsSamePathOrWithin(p!, folderPath)))
            {
                result.Warnings.Add("Refused to delete all files in the audiobook folder because other audiobook files are inside it.");
                return null;
            }

            var allAudiobooks = await _repo.GetAllAsync();
            var otherAudiobookPaths = allAudiobooks
                .Where(a => a.Id != audiobook.Id)
                .Select(a => new { a.Id, a.BasePath, a.FilePath })
                .ToList();

            foreach (var otherPath in otherAudiobookPaths)
            {
                var otherBasePath = NormalizePath(otherPath.BasePath);
                if (!string.IsNullOrWhiteSpace(otherBasePath)
                    && (IsSamePathOrWithin(otherBasePath, folderPath) || IsSamePathOrWithin(folderPath, otherBasePath)))
                {
                    result.Warnings.Add("Refused to delete all files in the audiobook folder because another audiobook references that location.");
                    return null;
                }

                var otherFilePath = NormalizePath(otherPath.FilePath);
                if (!string.IsNullOrWhiteSpace(otherFilePath) && IsSamePathOrWithin(otherFilePath, folderPath))
                {
                    result.Warnings.Add("Refused to delete all files in the audiobook folder because another audiobook file is inside it.");
                    return null;
                }
            }

            return new DeleteFolderTarget
            {
                FolderPath = folderPath,
                ProtectedRoots = protectedRoots
            };
        }

        private async Task TryDeleteAudiobookFolderAsync(Audiobook audiobook, DeleteFolderTarget deleteTarget, DeleteFilesystemResult result)
        {
            if (!Directory.Exists(deleteTarget.FolderPath))
            {
                return;
            }

            try
            {
                Directory.Delete(deleteTarget.FolderPath, recursive: true);
                result.DeletedFolder = true;
                _logger.LogInformation("Deleted audiobook folder {FolderPath}", LogRedaction.SanitizeFilePath(deleteTarget.FolderPath));
                await TryDeleteEmptyAuthorFolderAsync(audiobook, deleteTarget.FolderPath, deleteTarget.ProtectedRoots, result);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                result.Warnings.Add("Failed to delete the audiobook folder.");
                _logger.LogWarning(ex, "Failed to delete audiobook folder {FolderPath}", LogRedaction.SanitizeFilePath(deleteTarget.FolderPath));
            }
        }

        private async Task TryDeleteEmptyAuthorFolderAsync(
            Audiobook audiobook,
            string deletedFolderPath,
            IReadOnlyCollection<string> protectedRoots,
            DeleteFilesystemResult result)
        {
            var parentFolder = NormalizePath(Path.GetDirectoryName(deletedFolderPath));
            if (string.IsNullOrWhiteSpace(parentFolder)
                || IsFilesystemRoot(parentFolder)
                || protectedRoots.Any(root => PathsEqual(root, parentFolder))
                || !Directory.Exists(parentFolder)
                || !IsAuthorFolder(parentFolder, audiobook.Authors?.FirstOrDefault()))
            {
                return;
            }

            try
            {
                if (Directory.EnumerateFileSystemEntries(parentFolder).Any())
                {
                    return;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                _logger.LogDebug(ex, "Unable to inspect parent folder {FolderPath} after audiobook delete", LogRedaction.SanitizeFilePath(parentFolder));
                return;
            }

            var allAbs = await _repo.GetAllAsync();
            var otherAudiobookPaths = allAbs
                .Where(a => a.Id != audiobook.Id)
                .Select(a => new { a.Id, a.BasePath, a.FilePath })
                .ToList();

            foreach (var otherPath in otherAudiobookPaths)
            {
                var otherBasePath = NormalizePath(otherPath.BasePath);
                if (!string.IsNullOrWhiteSpace(otherBasePath)
                    && (IsSamePathOrWithin(otherBasePath, parentFolder) || IsSamePathOrWithin(parentFolder, otherBasePath)))
                {
                    return;
                }

                var otherFilePath = NormalizePath(otherPath.FilePath);
                if (!string.IsNullOrWhiteSpace(otherFilePath) && IsSamePathOrWithin(otherFilePath, parentFolder))
                {
                    return;
                }
            }

            try
            {
                Directory.Delete(parentFolder, recursive: false);
                result.DeletedParentFolder = true;
                _logger.LogInformation("Deleted empty parent author folder {FolderPath}", LogRedaction.SanitizeFilePath(parentFolder));
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                result.Warnings.Add("Failed to delete the empty author folder.");
                _logger.LogWarning(ex, "Failed to delete empty parent author folder {FolderPath}", LogRedaction.SanitizeFilePath(parentFolder));
            }
        }

        private async Task<HashSet<string>> GetProtectedRootPathsAsync()
        {
            var protectedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                if (_rootFolderService != null)
                {
                    var roots = await _rootFolderService.GetAllAsync();
                    foreach (var normalizedRoot in roots
                        .Select(root => NormalizePath(root.Path))
                        .Where(normalizedRoot => !string.IsNullOrWhiteSpace(normalizedRoot)))
                    {
                        protectedRoots.Add(normalizedRoot!);
                    }
                }
                else
                {
                    var roots = (await _rootFolderRepository.GetAllAsync()).Select(r => r.Path).ToList();

                    foreach (var normalizedRoot in roots
                        .Select(root => NormalizePath(root))
                        .Where(normalizedRoot => !string.IsNullOrWhiteSpace(normalizedRoot)))
                    {
                        protectedRoots.Add(normalizedRoot!);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to enumerate configured root folders while deleting audiobook files");
            }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var configService = scope.ServiceProvider.GetService<IConfigurationService>();
                if (configService != null)
                {
                    var settings = await configService.GetApplicationSettingsAsync();
                    var outputPath = NormalizePath(settings?.OutputPath);
                    if (!string.IsNullOrWhiteSpace(outputPath))
                    {
                        protectedRoots.Add(outputPath);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to load application settings while protecting root folders during delete");
            }

            return protectedRoots;
        }

        private static string? ResolveAudiobookFolderPath(Audiobook audiobook, IReadOnlyList<string> trackedFilePaths)
        {
            var basePath = NormalizePath(audiobook.BasePath);
            if (!string.IsNullOrWhiteSpace(basePath))
            {
                return basePath;
            }

            var legacyFilePath = NormalizePath(audiobook.FilePath);
            if (!string.IsNullOrWhiteSpace(legacyFilePath))
            {
                return NormalizePath(Path.GetDirectoryName(legacyFilePath));
            }

            return GetCommonDirectoryPath(trackedFilePaths);
        }

        private static string? ResolveTrackedFolderPath(IReadOnlyList<string> trackedFilePaths)
        {
            if (trackedFilePaths.Count == 0)
            {
                return null;
            }

            if (trackedFilePaths.Count == 1)
            {
                var directFolder = NormalizePath(Path.GetDirectoryName(trackedFilePaths[0]));
                if (string.IsNullOrWhiteSpace(directFolder))
                {
                    return null;
                }

                var folderName = Path.GetFileName(directFolder);
                if (IsLikelySegmentFolder(folderName))
                {
                    var parentFolder = NormalizePath(Path.GetDirectoryName(directFolder));
                    if (!string.IsNullOrWhiteSpace(parentFolder))
                    {
                        return parentFolder;
                    }
                }

                return directFolder;
            }

            return GetCommonDirectoryPath(trackedFilePaths);
        }

        private static bool IsLikelySegmentFolder(string? folderName)
        {
            if (string.IsNullOrWhiteSpace(folderName))
            {
                return false;
            }

            return Regex.IsMatch(
                folderName.Trim(),
                @"^(disc|disk|cd|part|chapter|track)[\s._-]*\d+$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        private static string? GetCommonDirectoryPath(IReadOnlyList<string> filePaths)
        {
            if (filePaths.Count == 0)
            {
                return null;
            }

            var directories = filePaths
                .Select(p => NormalizePath(Path.GetDirectoryName(p)))
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (directories.Count == 0)
            {
                return null;
            }

            var commonPath = directories[0];
            for (var i = 1; i < directories.Count; i++)
            {
                while (!IsSamePathOrWithin(directories[i], commonPath))
                {
                    var parent = NormalizePath(Path.GetDirectoryName(commonPath));
                    if (string.IsNullOrWhiteSpace(parent) || PathsEqual(parent, commonPath))
                    {
                        return null;
                    }

                    commonPath = parent;
                }
            }

            return IsFilesystemRoot(commonPath) ? null : commonPath;
        }

        private static string? NormalizePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            try
            {
                return FileUtils.NormalizeStoredPath(path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch (ArgumentException)
            {
                return null;
            }
        }

        private static bool PathsEqual(string? left, string? right)
        {
            if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            {
                return false;
            }

            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSamePathOrWithin(string path, string rootPath)
        {
            return PathsEqual(path, rootPath) || FileUtils.IsPathInsideOf(path, rootPath);
        }

        private static bool IsFilesystemRoot(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var root = NormalizePath(Path.GetPathRoot(path));
            return !string.IsNullOrWhiteSpace(root) && PathsEqual(root, path);
        }

        private static bool IsAuthorFolder(string folderPath, string? authorName)
        {
            if (string.IsNullOrWhiteSpace(folderPath) || string.IsNullOrWhiteSpace(authorName))
            {
                return false;
            }

            var folderName = Path.GetFileName(folderPath);
            return NormalizeName(folderName) == NormalizeName(authorName);
        }

        private static string NormalizeName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var cleaned = new string(value
                .Where(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c))
                .ToArray());

            return string.Join(
                ' ',
                cleaned.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                .ToLowerInvariant();
        }

        private static string BuildDeleteMessage(DeleteFilesystemResult? result)
        {
            if (result == null)
            {
                return "Audiobook deleted successfully.";
            }

            var cleanupParts = new List<string>();
            if (result.DeletedFiles > 0)
            {
                cleanupParts.Add($"removed {result.DeletedFiles} file{(result.DeletedFiles == 1 ? string.Empty : "s")}");
            }

            if (result.DeletedFolder)
            {
                cleanupParts.Add("deleted the audiobook folder");
            }

            if (result.DeletedParentFolder)
            {
                cleanupParts.Add("deleted the empty author folder");
            }

            var message = cleanupParts.Count > 0
                ? $"Audiobook deleted and {string.Join(" and ", cleanupParts)}."
                : "Audiobook deleted successfully.";

            if (result.Warnings.Count > 0)
            {
                message += " Some filesystem cleanup steps were skipped.";
            }

            return message;
        }

        /// <summary>
        /// Delete multiple audiobooks in a single transaction.
        /// </summary>
        /// <param name="request">List of audiobook IDs to delete.</param>
        /// <returns>Summary with deleted count, image cleanup count, and any per-item errors.</returns>
        [HttpPost("delete-bulk")]
        public async Task<IActionResult> BulkDeleteAudiobooks([FromBody] BulkDeleteRequest request)
        {
            if (request.Ids == null || !request.Ids.Any())
            {
                return BadRequest(new { message = "No audiobook IDs provided for bulk deletion" });
            }

            var deletedCount = 0;
            var deletedImagesCount = 0;
            var errors = new List<string>();
            var deletedIds = new List<int>();

            foreach (var id in request.Ids.Distinct())
            {
                try
                {
                    var audiobook = await _repo.GetByIdAsync(id);
                    if (audiobook == null)
                    {
                        errors.Add($"Audiobook with ID {id} not found");
                        continue;
                    }

                    // Delete associated image from cache if it exists
                    try
                    {
                        if (!string.IsNullOrEmpty(audiobook.Asin))
                        {
                            var imagePath = await _imageCacheService.GetCachedImagePathAsync(audiobook.Asin);
                            if (imagePath != null)
                            {
                                var fullPath = ResolvePathWithOptionalBase(Directory.GetCurrentDirectory(), imagePath);
                                if (System.IO.File.Exists(fullPath))
                                {
                                    System.IO.File.Delete(fullPath);
                                    deletedImagesCount++;
                                    _logger.LogInformation("Deleted cached image for ASIN {Asin}", LogRedaction.SanitizeText(audiobook.Asin));
                                }
                            }
                        }
                        else if (!string.IsNullOrEmpty(audiobook.ImageUrl))
                        {
                            try
                            {
                                // Safely extract identifier from an internal library image URL
                                const string __marker = "/config/cache/images/library/";
                                var __url = audiobook.ImageUrl;
                                var __idx = __url.IndexOf(__marker, StringComparison.OrdinalIgnoreCase);
                                if (__idx >= 0)
                                {
                                    var filename = __url.Substring(__idx + __marker.Length);
                                    filename = System.IO.Path.GetFileName(filename);
                                    var identifier = System.IO.Path.GetFileNameWithoutExtension(filename);

                                    if (!string.IsNullOrEmpty(identifier) && System.Text.RegularExpressions.Regex.IsMatch(identifier, "^[A-Za-z0-9_\\-\\.]{1,128}$"))
                                    {
                                        var imagePath = await _imageCacheService.GetCachedImagePathAsync(identifier);
                                        if (!string.IsNullOrEmpty(imagePath))
                                        {
                                            var fullPath = ResolvePathWithOptionalBase(Directory.GetCurrentDirectory(), imagePath);
                                            if (System.IO.File.Exists(fullPath))
                                            {
                                                System.IO.File.Delete(fullPath);
                                                deletedImagesCount++;
                                                _logger.LogInformation("Deleted cached image for identifier (from ImageUrl): {Identifier}", LogRedaction.SanitizeText(identifier));
                                            }
                                        }
                                    }
                                    else
                                    {
                                        _logger.LogWarning("Image identifier from ImageUrl for audiobook id {Id} is invalid: {Identifier}", audiobook.Id, LogRedaction.SanitizeText(identifier));
                                    }
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                            {
                                _logger.LogWarning(ex, "Failed to delete cached image based on stored ImageUrl for audiobook id {Id}", audiobook.Id);
                            }
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Failed to delete cached image for audiobook id {Id}", audiobook.Id);
                        // Continue with deletion even if image cleanup fails
                    }

                    // Log history entry for the deleted audiobook
                    var historyEntry = new History
                    {
                        AudiobookId = audiobook.Id,
                        AudiobookTitle = audiobook.Title ?? "Unknown Title",
                        EventType = "Deleted",
                        Message = $"Audiobook '{audiobook.Title}' deleted via bulk operation",
                        Source = "BulkDelete",
                        Timestamp = DateTime.UtcNow
                    };

                    await _historyRepository.AddAsync(historyEntry);

                    var deleted = await _repo.DeleteByIdAsync(id);
                    if (deleted)
                    {
                        deletedCount++;
                        deletedIds.Add(id);
                        _logger.LogInformation("Deleted audiobook '{Title}' (ID: {Id}) via bulk operation", LogRedaction.SanitizeText(audiobook.Title), id);
                    }
                    else
                    {
                        errors.Add($"Failed to delete audiobook with ID {id}");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogError(ex, "Error during bulk delete for ID {Id}: {Message}", id, ex.Message);
                    errors.Add($"Error deleting audiobook with ID {id}: {ex.Message}");
                }
            }

            if (deletedCount == 0 && errors.Any())
            {
                return BadRequest(new { message = "No audiobooks were successfully deleted", errors });
            }

            object result = errors.Any()
                ? new
                {
                    message = $"Partially successful: deleted {deletedCount} audiobook{(deletedCount != 1 ? "s" : "")}, {errors.Count} error{(errors.Count != 1 ? "s" : "")} occurred",
                    deletedCount,
                    deletedImagesCount,
                    ids = deletedIds,
                    errors
                }
                : new
                {
                    message = $"Successfully deleted {deletedCount} audiobook{(deletedCount != 1 ? "s" : "")}",
                    deletedCount,
                    deletedImagesCount,
                    ids = deletedIds
                };

            return Ok(result);
        }

        /// <summary>
        /// Preview duplicate audiobook groups in the library across two
        /// detection passes: same-ASIN (rows sharing a normalized UPPER/trimmed
        /// ASIN, <see cref="DuplicateGroupKind.Asin"/>) and same-canonical-target
        /// (rows that resolve to the same <c>{Author}/{Title}/…</c> folder via
        /// the configured <c>FolderNamingPattern</c> but have distinct ASINs,
        /// <see cref="DuplicateGroupKind.TitleAuthor"/>). The title/author pass
        /// catches edition variants and wrong-metadata rows the same-ASIN pass
        /// can't see.
        /// </summary>
        /// <remarks>
        /// Sequential-resolution semantic: a row already in an ASIN group is
        /// excluded from the title/author pass. After the user merges the ASIN
        /// pair, a re-fetch surfaces any residual title/author collisions
        /// involving the survivor. This keeps each row in exactly one group at
        /// a time so the UI never has to disambiguate.
        /// </remarks>
        [HttpGet("duplicates")]
        public async Task<IActionResult> GetDuplicates()
        {
            var allAudiobooks = await _repo.GetAllAsync();
            // Find the same-ASIN in-scope ids first.
            var asinGroupedIds = allAudiobooks
                .Where(a => !string.IsNullOrWhiteSpace(a.Asin))
                .GroupBy(a => a.Asin!.Trim().ToUpperInvariant())
                .Where(g => g.Count() > 1)
                .SelectMany(g => g.Select(a => a.Id))
                .ToHashSet();

            // Settings + roots are needed for the title/author pass below.
            // Load them now so the same instances feed every row's target
            // computation.
            using var scope = _scopeFactory.CreateScope();
            var configService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
            var settings = await configService.GetApplicationSettingsAsync();
            var rootFolders = _rootFolderService != null
                ? await _rootFolderService.GetAllAsync()
                : new List<RootFolder>();

            // Title/author pass: rows NOT in any ASIN group whose computed
            // canonical target collides with at least one other row. Building
            // the map up front lets us include those ids in the file-loading
            // scope so their detail rows look identical to the ASIN ones.
            var titleAuthorTargetByAudiobookId = new Dictionary<int, string>();
            foreach (var audiobook in allAudiobooks)
            {
                if (asinGroupedIds.Contains(audiobook.Id)) continue;
                var (target, invalidReason) = ComputeOrganizeTarget(audiobook, settings, rootFolders);
                if (!string.IsNullOrEmpty(invalidReason) || string.IsNullOrWhiteSpace(target)) continue;
                titleAuthorTargetByAudiobookId[audiobook.Id] = NormalizeOrganizeKey(target);
            }
            var titleAuthorGroupedIds = titleAuthorTargetByAudiobookId
                .GroupBy(kv => kv.Value)
                .Where(g => g.Count() > 1)
                .SelectMany(g => g.Select(kv => kv.Key))
                .ToHashSet();

            // Pre-compute the file map for every in-scope id (both passes) so
            // the response can surface per-row size / bitrate / format for
            // verifying "is this really the same book?" before merging.
            var inScopeAudiobookIds = new HashSet<int>(asinGroupedIds);
            inScopeAudiobookIds.UnionWith(titleAuthorGroupedIds);
            var allFiles = await _audioFileRepository.GetAllAsync();
            var filesByAudiobookId = allFiles
                .Where(f => inScopeAudiobookIds.Contains(f.AudiobookId))
                .GroupBy(f => f.AudiobookId)
                .ToDictionary(g => g.Key, g => g.ToList());

            // Build a group DTO from the audiobook rows that share a key —
            // identical row-construction and winner-selection logic across both
            // the ASIN and title/author passes. Returns null when the group
            // becomes empty after row construction (shouldn't happen because
            // both passes filter to size>1, but defensive).
            DuplicateGroupDto? BuildGroup(IList<Audiobook> members, string kind, string normalizedAsin, string collisionKey)
            {
                var rows = members
                    .Select(a =>
                    {
                        // Sort files by path so chapters list in a stable order.
                        // Zero-padded numeric prefixes (01, 02, ... 10) sort correctly
                        // alphabetically; the audiobook detail view does richer natural
                        // sort, but for a preview modal alphabetical is fine.
                        var files = filesByAudiobookId.TryGetValue(a.Id, out var fs)
                            ? fs.OrderBy(f => f.Path ?? string.Empty, StringComparer.Ordinal).ToList()
                            : new List<AudiobookFile>();
                        var fileCount = files.Count;
                        var hasAnyFile = fileCount > 0 || !string.IsNullOrWhiteSpace(a.FilePath);
                        return new DuplicateRowDto
                        {
                            Id = a.Id,
                            Title = a.Title,
                            Series = a.Series,
                            SeriesNumber = a.SeriesNumber,
                            Asin = a.Asin,
                            BasePath = a.BasePath,
                            FilePath = a.FilePath,
                            ImageUrl = a.ImageUrl,
                            FileCount = fileCount,
                            HasAnyFile = hasAnyFile,
                            HasBookFolder = HasRealBookFolder(a.BasePath),
                            Authors = a.Authors ?? new List<string>(),
                            Narrators = a.Narrators ?? new List<string>(),
                            Runtime = a.Runtime,
                            Files = files.Select(f => new DuplicateFileDto
                            {
                                Id = f.Id,
                                Path = f.Path,
                                Size = f.Size,
                                DurationSeconds = f.DurationSeconds,
                                Format = f.Format,
                                Codec = f.Codec,
                                Bitrate = f.Bitrate,
                            }).ToList(),
                            TotalSize = files.Sum(f => f.Size ?? 0L),
                            LikelyDuplicateFileCount = CountIntraRowDuplicates(files),
                        };
                    })
                    .ToList();
                if (rows.Count == 0) return null;

                // Compute a "does this row's folder look like it actually
                // belongs to this book" score per row. Captures the
                // common "wrong ASIN got stamped on this row" symptom
                // where the same ASIN ends up on rows whose BasePath
                // points at totally different authors/books — the row
                // whose folder name matches the title and/or author is
                // the one we want. (For title/author groups the score is
                // less discriminating since both rows resolve to the same
                // canonical target, but it still tiebreaks usefully when
                // one row's BasePath drifts from its metadata.)
                var audiobooksById = members.ToDictionary(a => a.Id);
                var pathScoreById = rows.ToDictionary(
                    r => r.Id,
                    r => PathMetadataMatchScore(audiobooksById[r.Id]));

                // Bitrate per row — max across the row's tracked files,
                // 0 when no files / no bitrate data. Used as the
                // primary quality signal for choosing a winner.
                var maxBitrateById = rows.ToDictionary(
                    r => r.Id,
                    r => r.Files.Where(f => f.Bitrate.HasValue).Select(f => f.Bitrate!.Value).DefaultIfEmpty(0).Max());

                // Effective unique file count: total files minus the count
                // of files that look like duplicate naming variants of
                // another file on the same row. Two rows that both
                // represent a 57-chapter book — one of them with each
                // chapter imported twice in different naming styles —
                // should be treated as equally complete, not 114 > 57.
                var effectiveFileCountById = rows.ToDictionary(
                    r => r.Id,
                    r => r.FileCount - r.LikelyDuplicateFileCount);

                // Winner-selection rule, in order of importance:
                //  1. Has any file (a row with no playable file is never
                //     a winner unless every row is fileless).
                //  2. Higher bitrate — quality trumps shape; a clean
                //     192 kbps multi-file is preferable to a 32 kbps
                //     single file of the same book.
                //  3. Single-file (FileCount == 1) — Kevin's stated
                //     preference among rows of equal quality.
                //  4. More unique tracked files (FileCount minus
                //     LikelyDuplicateFileCount). A 114-file row whose
                //     filenames pair up as 57×2 naming variants is
                //     effectively 57, not 114.
                //  5. Fewer intra-row dupes (cleaner content beats a
                //     row that needs deduping after merge).
                //  6. Real book folder shape.
                //  7. Path-metadata alignment (folder name matches the
                //     book's title/author — catches stray ASINs that
                //     landed on the wrong row).
                //  8. Lowest Id (stable tiebreaker).
                var orderedForWinner = rows
                    .OrderByDescending(r => r.HasAnyFile)
                    .ThenByDescending(r => maxBitrateById[r.Id])
                    .ThenByDescending(r => r.FileCount == 1)
                    .ThenByDescending(r => effectiveFileCountById[r.Id])
                    .ThenBy(r => r.LikelyDuplicateFileCount)
                    .ThenByDescending(r => r.HasBookFolder)
                    .ThenByDescending(r => pathScoreById[r.Id])
                    .ThenBy(r => r.Id)
                    .ToList();
                var winner = orderedForWinner.First();
                winner.RecommendedWinner = true;

                // For title/author groups we phrase the recommendation
                // differently: the same heuristic picks a likely-canonical
                // row, but the user's action is "delete the misnamed
                // rows" rather than "merge into the winner" since each row
                // has a distinct ASIN. The merge endpoint will reject any
                // cross-ASIN attempt anyway.
                var recommendation = kind == DuplicateGroupKind.TitleAuthor
                    ? "Same author/title — different ASINs. Open each row in a new tab to compare and clean up the wrong-metadata or duplicate-edition row(s) via the audiobook detail page."
                    : ExplainRecommendation(winner, rows, pathScoreById, maxBitrateById);

                return new DuplicateGroupDto
                {
                    Kind = kind,
                    NormalizedAsin = normalizedAsin,
                    CollisionKey = collisionKey,
                    Rows = rows.OrderBy(r => r.Id).ToList(),
                    RecommendationReason = recommendation,
                };
            }

            var asinGroups = allAudiobooks
                .Where(a => !string.IsNullOrWhiteSpace(a.Asin))
                .GroupBy(a => a.Asin!.Trim().ToUpperInvariant())
                .Where(g => g.Count() > 1)
                .OrderBy(g => g.Key)
                .Select(g => BuildGroup(g.ToList(), DuplicateGroupKind.Asin, g.Key, string.Empty))
                .Where(group => group != null)
                .Cast<DuplicateGroupDto>()
                .ToList();

            // Title/author pass: group the pre-computed target map and emit
            // one group per collision key. Sorted by target key for stable UI
            // ordering.
            //
            // Phantom-row filtering: a row with zero tracked AudiobookFiles
            // is a monitored-but-not-downloaded record — it has no content to
            // compare and never sits on disk at the canonical target. When it
            // shares a target with at least one row that *does* have files,
            // the phantom is just noise (the user's options would be "delete
            // the empty wishlist record" — no real choice). Drop it. Groups
            // where ALL rows are fileless still surface, because two wishlist
            // records under different ASINs for the same book IS a real
            // duplicate the user needs to resolve.
            var audiobooksByIdLookup = allAudiobooks.ToDictionary(a => a.Id);
            var fileCountByAudiobookId = filesByAudiobookId
                .ToDictionary(kv => kv.Key, kv => kv.Value.Count);
            int FileCount(int id) => fileCountByAudiobookId.TryGetValue(id, out var n) ? n : 0;

            var titleAuthorGroups = titleAuthorTargetByAudiobookId
                .GroupBy(kv => kv.Value)
                .Where(g => g.Count() > 1)
                .Select(g =>
                {
                    var memberIds = g.Select(kv => kv.Key).ToList();
                    var anyHasFiles = memberIds.Any(id => FileCount(id) > 0);
                    var filteredIds = anyHasFiles
                        ? memberIds.Where(id => FileCount(id) > 0).ToList()
                        : memberIds;
                    return new { Key = g.Key, Ids = filteredIds };
                })
                .Where(x => x.Ids.Count > 1)
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => BuildGroup(
                    x.Ids.Select(id => audiobooksByIdLookup[id]).ToList(),
                    DuplicateGroupKind.TitleAuthor,
                    normalizedAsin: string.Empty,
                    collisionKey: x.Key))
                .Where(group => group != null)
                .Cast<DuplicateGroupDto>()
                .ToList();

            var groups = asinGroups.Concat(titleAuthorGroups).ToList();
            return Ok(new
            {
                groups,
                totalGroups = groups.Count,
                asinGroupCount = asinGroups.Count,
                titleAuthorGroupCount = titleAuthorGroups.Count,
            });
        }

        /// <summary>
        /// Produce a short, user-facing reason describing why
        /// <paramref name="winner"/> was preferred over the other rows. Mirrors
        /// the ordering rules in <see cref="GetDuplicates"/> so the UI shows
        /// truth rather than a generic blurb.
        /// </summary>
        private static string ExplainRecommendation(
            DuplicateRowDto winner,
            List<DuplicateRowDto> rows,
            Dictionary<int, int> pathScoreById,
            Dictionary<int, int> maxBitrateById)
        {
            var others = rows.Where(r => r.Id != winner.Id).ToList();
            if (winner.HasAnyFile && others.All(r => !r.HasAnyFile))
            {
                return "Only row with tracked files.";
            }
            // Bitrate is the primary quality signal — a clean high-bitrate
            // copy beats a same-shape low-bitrate copy.
            var winnerBitrate = maxBitrateById[winner.Id];
            var bestOtherBitrate = others.Any() ? others.Max(r => maxBitrateById[r.Id]) : 0;
            if (winnerBitrate > 0 && winnerBitrate > bestOtherBitrate)
            {
                return $"Higher bitrate ({winnerBitrate / 1000} kbps vs {bestOtherBitrate / 1000} kbps).";
            }
            // Single-file copies are preferred over chapter-file imports
            // of equal-bitrate.
            if (winner.HasAnyFile && winner.FileCount == 1 && others.Any(r => r.FileCount > 1))
            {
                return "Single-file copy (preferred over chapter-file imports of equal bitrate).";
            }
            // Two rows might "tie" on raw count but the winner has cleaner
            // content (fewer naming-variant duplicates of the same chapter).
            if (winner.HasAnyFile && others.Any(r => r.HasAnyFile)
                && winner.LikelyDuplicateFileCount == 0
                && others.Any(r => r.LikelyDuplicateFileCount > 0))
            {
                var dirtiest = others.OrderByDescending(r => r.LikelyDuplicateFileCount).First();
                return $"Cleaner file list (no naming-variant duplicates; other rows have up to {dirtiest.LikelyDuplicateFileCount} likely dupes).";
            }
            if (winner.HasAnyFile && others.Any(r => r.HasAnyFile) && winner.FileCount > others.Max(r => r.FileCount))
            {
                return $"Most tracked files ({winner.FileCount} vs {others.Max(r => r.FileCount)}).";
            }
            if (winner.HasBookFolder && others.All(r => !r.HasBookFolder))
            {
                return "Only row with a real book folder; others have just an author or root folder.";
            }
            // Path-metadata alignment: the row whose BasePath actually
            // contains the title/author wins when others tie on the earlier
            // signals. Common case: ASIN got cross-stamped onto a row whose
            // folder belongs to a totally different book.
            var winnerScore = pathScoreById[winner.Id];
            var bestOtherScore = others.Any() ? others.Max(r => pathScoreById[r.Id]) : 0;
            if (winnerScore > 0 && winnerScore > bestOtherScore)
            {
                return "Folder path matches the book's title/author metadata; the other rows' folders don't.";
            }
            if (!winner.HasAnyFile && !winner.HasBookFolder)
            {
                return "All rows are file-less and lack a real book folder; falling back to lowest Id.";
            }
            return "Tied on every signal; falling back to lowest Id.";
        }

        /// <summary>
        /// Score how well <paramref name="audiobook"/>'s BasePath aligns with
        /// its title/authors metadata. Used as a tiebreaker in
        /// <see cref="GetDuplicates"/> so when two same-ASIN rows have
        /// different folder shapes (one whose folder name matches the book,
        /// one whose folder name belongs to a different author entirely), the
        /// one that "belongs there" wins.
        /// </summary>
        /// <returns>0 = no match; 1 = author match; 2 = title match; 3 = both.</returns>
        private static int PathMetadataMatchScore(Audiobook audiobook)
        {
            if (string.IsNullOrWhiteSpace(audiobook.BasePath)) return 0;
            var pathNorm = NormalizeForPathMatch(audiobook.BasePath);
            if (string.IsNullOrEmpty(pathNorm)) return 0;

            var score = 0;
            var titleNorm = NormalizeForPathMatch(audiobook.Title ?? string.Empty);
            if (titleNorm.Length >= 5 && pathNorm.Contains(titleNorm, StringComparison.Ordinal))
            {
                score += 2;
            }
            if (audiobook.Authors != null)
            {
                foreach (var author in audiobook.Authors)
                {
                    var authorNorm = NormalizeForPathMatch(author ?? string.Empty);
                    if (authorNorm.Length >= 3 && pathNorm.Contains(authorNorm, StringComparison.Ordinal))
                    {
                        score += 1;
                        break;
                    }
                }
            }
            return score;
        }

        private static string NormalizeForPathMatch(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new StringBuilder(s.Length);
            foreach (var ch in s)
            {
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString();
        }

        /// <summary>
        /// Count files on a row that look like they're paired up as the same
        /// chapter imported in two naming styles (e.g.
        /// <c>"01 Prologue_ Fortress of the Light.mp3"</c> and
        /// <c>"01. Prologue -  Fortress of the Light.mp3"</c>). Returns the
        /// count of "extra" files — i.e. <c>totalFiles - distinctSignatures</c>,
        /// so a row of 114 files with 57 unique normalized filenames reports
        /// 57 likely duplicates.
        /// </summary>
        private static int CountIntraRowDuplicates(IReadOnlyCollection<AudiobookFile> files)
        {
            if (files == null || files.Count <= 1) return 0;
            var signatures = new HashSet<string>(StringComparer.Ordinal);
            var counted = 0;
            foreach (var f in files)
            {
                var sig = NormalizeForFilenameMatch(System.IO.Path.GetFileNameWithoutExtension(f.Path ?? string.Empty));
                if (string.IsNullOrEmpty(sig)) { counted++; continue; }
                if (!signatures.Add(sig)) counted++;
            }
            return counted;

            static string NormalizeForFilenameMatch(string s)
            {
                if (string.IsNullOrEmpty(s)) return string.Empty;
                // Lowercase, strip non-alphanumeric (so "_", "-", ".", " " all
                // collapse to the same separator), then collapse runs of
                // letters/digits. The two naming styles
                //   "01 Prologue_ Fortress of the Light"
                //   "01. Prologue -  Fortress of the Light"
                // both collapse to "01prologuefortressofthelight".
                var sb = new StringBuilder(s.Length);
                foreach (var ch in s)
                {
                    if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
                }
                return sb.ToString();
            }
        }

        /// <summary>
        /// Merge same-ASIN duplicate audiobook groups in a single transaction.
        /// For each pair, reassigns Downloads / History / MoveJobs references
        /// from each loser to the winner, then deletes the loser audiobook
        /// rows (cascade removes their tracked file rows, external
        /// identifiers, and series memberships). Files on disk are NOT
        /// touched — that's a separate cleanup the user manages explicitly.
        /// </summary>
        /// <remarks>
        /// Validates that every winner+losers tuple shares the same normalized
        /// ASIN before touching anything. If any tuple fails validation the
        /// whole merge is aborted.
        /// </remarks>
        [HttpPost("duplicates/merge")]
        public async Task<IActionResult> MergeDuplicates([FromBody] MergeDuplicatesRequest request)
        {
            if (request?.Merges == null || request.Merges.Count == 0)
            {
                return BadRequest(new { message = "No merges provided" });
            }

            // Strip out trivially-empty pairs first so we don't open a DB
            // transaction just to do nothing.
            var nonEmptyMerges = request.Merges
                .Where(m =>
                    (m.LoserIds != null && m.LoserIds.Any(id => id != (m.WinnerId ?? -1))) ||
                    (m.ClearAsinIds != null && m.ClearAsinIds.Count > 0))
                .ToList();
            if (nonEmptyMerges.Count == 0)
            {
                return BadRequest(new { message = "No actionable merges or clear-ASIN entries" });
            }

            var ct = HttpContext?.RequestAborted ?? CancellationToken.None;
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ListenArrDbContext>();

            // Validation pass: every winner + losers + clearAsinIds tuple must
            // share the same normalized ASIN. Aborts the whole request on
            // first mismatch so the user can fix their selection before
            // retrying.
            var allIds = nonEmptyMerges
                .SelectMany(m =>
                    (m.WinnerId.HasValue ? new[] { m.WinnerId.Value } : Array.Empty<int>())
                    .Concat(m.LoserIds ?? new List<int>())
                    .Concat(m.ClearAsinIds ?? new List<int>()))
                .Distinct()
                .ToList();
            var rowsById = await db.Audiobooks
                .AsNoTracking()
                .Where(a => allIds.Contains(a.Id))
                .Select(a => new { a.Id, a.Asin })
                .ToDictionaryAsync(a => a.Id, ct);

            foreach (var pair in nonEmptyMerges)
            {
                var losers = (pair.LoserIds ?? new List<int>()).Distinct().Where(id => id != (pair.WinnerId ?? -1)).ToList();
                var clears = (pair.ClearAsinIds ?? new List<int>()).Distinct().ToList();

                // Pick a reference ASIN: the winner's if present, otherwise any
                // loser's, otherwise any clear's. All other ids in the pair
                // must agree with it.
                string referenceAsin = string.Empty;
                int referenceId = 0;
                if (pair.WinnerId.HasValue)
                {
                    if (!rowsById.TryGetValue(pair.WinnerId.Value, out var w))
                    {
                        return BadRequest(new { message = $"Winner id {pair.WinnerId} not found" });
                    }
                    referenceAsin = (w.Asin ?? string.Empty).Trim().ToUpperInvariant();
                    if (string.IsNullOrEmpty(referenceAsin))
                    {
                        return BadRequest(new { message = $"Winner id {pair.WinnerId} has no ASIN — refusing to merge without one" });
                    }
                    referenceId = pair.WinnerId.Value;
                }
                else if (losers.Count > 0)
                {
                    return BadRequest(new { message = "Pair has losers but no winnerId — losers must merge into a winner" });
                }
                else if (clears.Count > 0)
                {
                    // ASIN-clear-only pair: any clear id supplies the reference
                    // ASIN, every other must match.
                    var firstClearId = clears[0];
                    if (!rowsById.TryGetValue(firstClearId, out var c))
                    {
                        return BadRequest(new { message = $"Clear-ASIN id {firstClearId} not found" });
                    }
                    referenceAsin = (c.Asin ?? string.Empty).Trim().ToUpperInvariant();
                    if (string.IsNullOrEmpty(referenceAsin))
                    {
                        return BadRequest(new { message = $"Clear-ASIN id {firstClearId} already has no ASIN" });
                    }
                    referenceId = firstClearId;
                }

                foreach (var loserId in losers)
                {
                    if (!rowsById.TryGetValue(loserId, out var loser))
                    {
                        return BadRequest(new { message = $"Loser id {loserId} not found" });
                    }
                    var loserAsin = (loser.Asin ?? string.Empty).Trim().ToUpperInvariant();
                    if (loserAsin != referenceAsin)
                    {
                        return BadRequest(new
                        {
                            message = $"Loser id {loserId} ASIN {loserAsin} does not match reference id {referenceId} ASIN {referenceAsin}"
                        });
                    }
                }
                foreach (var clearId in clears)
                {
                    if (!rowsById.TryGetValue(clearId, out var c))
                    {
                        return BadRequest(new { message = $"Clear-ASIN id {clearId} not found" });
                    }
                    var clearAsin = (c.Asin ?? string.Empty).Trim().ToUpperInvariant();
                    if (clearAsin != referenceAsin)
                    {
                        return BadRequest(new
                        {
                            message = $"Clear-ASIN id {clearId} ASIN {clearAsin} does not match reference id {referenceId} ASIN {referenceAsin}"
                        });
                    }
                }
            }

            var result = new MergeDuplicatesResultDto();

            // Pre-transaction: filesystem cleanup for every Discard target.
            // The DeleteAudiobookFilesystemAsync helper is best-effort —
            // failures (missing folder, perms, etc.) become warnings and the
            // DB delete still proceeds, matching how the single-row
            // DeleteAudiobook endpoint behaves. We do this BEFORE the DB
            // transaction because filesystem ops aren't transactional anyway;
            // partial filesystem state is acceptable, but a DB-only delete
            // that leaves orphan files would be worse.
            var allLoserIds = nonEmptyMerges
                .SelectMany(p => (p.LoserIds ?? new List<int>())
                    .Distinct()
                    .Where(id => id != (p.WinnerId ?? -1)))
                .Distinct()
                .ToList();
            if (allLoserIds.Count > 0)
            {
                var loserAudiobooks = await db.Audiobooks
                    .AsNoTracking()
                    .Include(a => a.Files)
                    .Where(a => allLoserIds.Contains(a.Id))
                    .ToListAsync(ct);
                foreach (var loser in loserAudiobooks)
                {
                    try
                    {
                        var fsResult = await DeleteAudiobookFilesystemAsync(loser, deleteFolder: true);
                        result.DiskFilesDeleted += fsResult.DeletedFiles;
                        if (fsResult.DeletedFolder) result.DiskFoldersDeleted++;
                        if (fsResult.DeletedParentFolder) result.DiskParentFoldersDeleted++;
                        foreach (var w in fsResult.Warnings)
                        {
                            result.Warnings.Add($"audiobook id {loser.Id}: {w}");
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Filesystem cleanup failed for discarded audiobook {Id}", loser.Id);
                        result.Warnings.Add($"audiobook id {loser.Id}: filesystem cleanup failed: {ex.Message}");
                    }
                }
            }

            // Execute DB ops inside one transaction. Reassignments are
            // simple UPDATE on plain FK columns (no DB constraint); the
            // audiobook delete cascades to AudiobookFiles,
            // AudiobookExternalIdentifiers, and AudiobookSeriesMemberships per
            // their FK definitions. Clear-ASIN nulls the column on the row.
            // The InMemory test provider doesn't support transactions; skip
            // the wrapper there so the unit tests can exercise the logic.
            var supportsTransactions = !string.Equals(
                db.Database.ProviderName,
                "Microsoft.EntityFrameworkCore.InMemory",
                StringComparison.Ordinal);
            Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? tx = supportsTransactions
                ? await db.Database.BeginTransactionAsync(ct)
                : null;
            try
            {
                foreach (var pair in nonEmptyMerges)
                {
                    var losers = (pair.LoserIds ?? new List<int>()).Distinct().Where(id => id != (pair.WinnerId ?? -1)).ToList();
                    var clears = (pair.ClearAsinIds ?? new List<int>()).Distinct().Except(losers).ToList();
                    if (losers.Count == 0 && clears.Count == 0) continue;

                    // Reassign FK refs from losers to winner, then delete.
                    // ExecuteUpdateAsync emits a bare UPDATE without
                    // SELECTing the row, so it sidesteps any column-level
                    // schema drift between model and DB (e.g. MoveJob.SourcePath
                    // exists on the model but isn't in the SQLite schema yet).
                    // The InMemory test provider doesn't support ExecuteUpdate,
                    // so fall back to load+update+save when supportsTransactions
                    // is false.
                    if (losers.Count > 0 && pair.WinnerId.HasValue)
                    {
                        var winnerId = pair.WinnerId.Value;

                        if (supportsTransactions)
                        {
                            result.DownloadsReassigned += await db.Downloads
                                .Where(d => d.AudiobookId != null && losers.Contains(d.AudiobookId.Value))
                                .ExecuteUpdateAsync(s => s.SetProperty(d => d.AudiobookId, winnerId), ct);

                            result.HistoryReassigned += await db.History
                                .Where(h => h.AudiobookId != null && losers.Contains(h.AudiobookId.Value))
                                .ExecuteUpdateAsync(s => s.SetProperty(h => h.AudiobookId, winnerId), ct);

                            result.MoveJobsReassigned += await db.MoveJobs
                                .Where(j => losers.Contains(j.AudiobookId))
                                .ExecuteUpdateAsync(s => s.SetProperty(j => j.AudiobookId, winnerId), ct);

                            result.RowsDeleted += await db.Audiobooks
                                .Where(a => losers.Contains(a.Id))
                                .ExecuteDeleteAsync(ct);
                        }
                        else
                        {
                            var downloads = await db.Downloads
                                .Where(d => d.AudiobookId != null && losers.Contains(d.AudiobookId.Value))
                                .ToListAsync(ct);
                            foreach (var d in downloads) d.AudiobookId = winnerId;
                            result.DownloadsReassigned += downloads.Count;

                            var historyRows = await db.History
                                .Where(h => h.AudiobookId != null && losers.Contains(h.AudiobookId.Value))
                                .ToListAsync(ct);
                            foreach (var h in historyRows) h.AudiobookId = winnerId;
                            result.HistoryReassigned += historyRows.Count;

                            var moveJobs = await db.MoveJobs
                                .Where(j => losers.Contains(j.AudiobookId))
                                .ToListAsync(ct);
                            foreach (var j in moveJobs) j.AudiobookId = winnerId;
                            result.MoveJobsReassigned += moveJobs.Count;

                            var loserRows = await db.Audiobooks
                                .Where(a => losers.Contains(a.Id))
                                .ToListAsync(ct);
                            db.Audiobooks.RemoveRange(loserRows);
                            result.RowsDeleted += loserRows.Count;
                        }
                    }

                    // Clear ASIN on rows the user wants to keep but un-dupe.
                    if (clears.Count > 0)
                    {
                        if (supportsTransactions)
                        {
                            result.AsinsCleared += await db.Audiobooks
                                .Where(a => clears.Contains(a.Id))
                                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Asin, (string?)null), ct);
                        }
                        else
                        {
                            var clearRows = await db.Audiobooks
                                .Where(a => clears.Contains(a.Id))
                                .ToListAsync(ct);
                            foreach (var c in clearRows) c.Asin = null;
                            result.AsinsCleared += clearRows.Count;
                        }
                    }

                    await db.SaveChangesAsync(ct);

                    result.GroupsProcessed++;
                }

                if (tx != null) await tx.CommitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Duplicate-audiobook merge failed; transaction rolled back");
                if (tx != null) await tx.RollbackAsync(CancellationToken.None);
                return StatusCode(500, new { message = "Merge failed; no changes applied", error = ex.Message });
            }
            finally
            {
                if (tx != null) await tx.DisposeAsync();
            }

            _logger.LogInformation(
                "Resolved {Groups} duplicate audiobook groups: deleted {Deleted} rows, cleared {Cleared} ASINs, removed {DiskFiles} files / {DiskFolders} folders from disk, reassigned {Downloads} downloads / {History} history / {MoveJobs} move jobs",
                result.GroupsProcessed, result.RowsDeleted, result.AsinsCleared,
                result.DiskFilesDeleted, result.DiskFoldersDeleted,
                result.DownloadsReassigned, result.HistoryReassigned, result.MoveJobsReassigned);

            return Ok(result);
        }

        /// <summary>
        /// A "real" book folder is one that is not the root <c>/audiobooks</c>
        /// (or its trailing-slash variant) and is more than one level deeper
        /// than that root — i.e. it contains an author + book segment, not
        /// just the author folder created by an aborted import.
        /// </summary>
        private static bool HasRealBookFolder(string? basePath)
        {
            if (string.IsNullOrWhiteSpace(basePath)) return false;
            var trimmed = basePath.Trim().TrimEnd('/');
            if (string.IsNullOrEmpty(trimmed)) return false;
            if (string.Equals(trimmed, "/audiobooks", StringComparison.OrdinalIgnoreCase)) return false;
            // "/audiobooks/Some Author" → 2 segments after split on "/", no
            // book folder. "/audiobooks/Author/Book" → 3 segments, real folder.
            var segments = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length <= 1) return false;
            // If the path starts with /audiobooks and only has one extra
            // segment, that's the author folder only.
            if (segments[0].Equals("audiobooks", StringComparison.OrdinalIgnoreCase) && segments.Length < 3)
            {
                return false;
            }
            return true;
        }

        /// <summary>
        /// Walk every audiobook and bucket it into one of:
        /// <c>already_canonical</c>, <c>will_move</c>, <c>collision</c>, or
        /// <c>invalid_target</c>. Read-only — no DB or filesystem changes.
        /// Targets are computed by applying <c>FolderNamingPattern</c> to each
        /// row's metadata against the root folder that contains the row's
        /// current <c>BasePath</c> (longest-prefix match; default root when
        /// no current path).
        /// </summary>
        [HttpGet("organize/preview")]
        public async Task<IActionResult> GetOrganizePreview(CancellationToken ct = default)
        {
            using var scope = _scopeFactory.CreateScope();
            var configService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
            var settings = await configService.GetApplicationSettingsAsync();
            var rootFolders = _rootFolderService != null
                ? await _rootFolderService.GetAllAsync()
                : new List<RootFolder>();

            var allAudiobooks = await _repo.GetAllAsync();
            var allFiles = await _audioFileRepository.GetAllAsync();
            var filesByAudiobookId = allFiles
                .GroupBy(f => f.AudiobookId)
                .ToDictionary(g => g.Key, g => g.ToList());

            // First pass: compute target per audiobook + initial bucket
            // (invalid_target vs proposed). Collision detection happens after
            // because it needs the full target map.
            var rows = new List<OrganizePreviewRowDto>(allAudiobooks.Count);
            var targetGroups = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

            foreach (var audiobook in allAudiobooks)
            {
                ct.ThrowIfCancellationRequested();
                var files = filesByAudiobookId.TryGetValue(audiobook.Id, out var fs) ? fs : new List<AudiobookFile>();

                // Rows with no tracked files have nothing to organize. The
                // earlier version of this check only skipped rows when BOTH
                // BasePath was empty AND there were no files, which let through
                // monitored-but-not-downloaded records whose BasePath was
                // stamped at the root (e.g. "/audiobooks"). Those rows then
                // appeared in `will_move`, got default-selected, and failed
                // the MoveBackgroundService's source-path-exists check one by
                // one — generating per-job DB writes and SignalR pings but
                // doing no real work. Skip every fileless row entirely:
                // whatever BasePath says, there's no on-disk content to move.
                if (files.Count == 0)
                {
                    continue;
                }

                var currentPath = NormalizeOrganizePath(audiobook.BasePath);
                var row = new OrganizePreviewRowDto
                {
                    Id = audiobook.Id,
                    Title = audiobook.Title,
                    Author = audiobook.Authors?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a)),
                    CurrentPath = currentPath,
                    FileCount = files.Count,
                    TotalSize = files.Sum(f => f.Size ?? 0L),
                };

                var (target, invalidReason) = ComputeOrganizeTarget(audiobook, settings, rootFolders);
                if (!string.IsNullOrEmpty(invalidReason))
                {
                    row.Status = OrganizePreviewStatus.InvalidTarget;
                    row.Reason = invalidReason;
                    rows.Add(row);
                    continue;
                }

                row.TargetPath = target;
                var key = NormalizeOrganizeKey(target);
                if (!targetGroups.TryGetValue(key, out var members))
                {
                    members = new List<int>();
                    targetGroups[key] = members;
                }
                members.Add(audiobook.Id);
                rows.Add(row);
            }

            // Second pass: assign bucket from target groups.
            var collisionKeys = targetGroups
                .Where(kv => kv.Value.Count > 1)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

            foreach (var row in rows)
            {
                if (row.Status == OrganizePreviewStatus.InvalidTarget) continue;
                var key = NormalizeOrganizeKey(row.TargetPath ?? string.Empty);
                if (collisionKeys.ContainsKey(key))
                {
                    row.Status = OrganizePreviewStatus.Collision;
                    row.CollisionKey = key;
                    continue;
                }
                row.Status = NormalizeOrganizeKey(row.CurrentPath ?? string.Empty) == key
                    ? OrganizePreviewStatus.AlreadyCanonical
                    : OrganizePreviewStatus.WillMove;
            }

            var preview = new OrganizeLibraryPreviewDto
            {
                Rows = rows.OrderBy(r => r.Status switch
                    {
                        OrganizePreviewStatus.WillMove => 0,
                        OrganizePreviewStatus.Collision => 1,
                        OrganizePreviewStatus.InvalidTarget => 2,
                        _ => 3,
                    }).ThenBy(r => r.Author ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(r => r.Title ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                AlreadyCanonicalCount = rows.Count(r => r.Status == OrganizePreviewStatus.AlreadyCanonical),
                WillMoveCount = rows.Count(r => r.Status == OrganizePreviewStatus.WillMove),
                CollisionCount = rows.Count(r => r.Status == OrganizePreviewStatus.Collision),
                InvalidTargetCount = rows.Count(r => r.Status == OrganizePreviewStatus.InvalidTarget),
            };
            return Ok(preview);
        }

        /// <summary>
        /// Queue a per-book move for each id the user confirmed in the
        /// preview. Each id is re-validated against the live DB state before
        /// queuing: ids that no longer compute to <c>will_move</c>, or that
        /// collide with another id in the same request, are skipped with a
        /// warning rather than aborting the rest. Missing ids return
        /// BadRequest without queuing anything.
        /// </summary>
        [HttpPost("organize/apply")]
        public async Task<IActionResult> ApplyOrganize([FromBody] OrganizeLibraryApplyRequest request, CancellationToken ct = default)
        {
            if (request?.AudiobookIds == null || request.AudiobookIds.Count == 0)
            {
                return BadRequest(new { message = "No audiobook ids provided" });
            }
            if (_moveQueueService == null)
            {
                return StatusCode(503, new { message = "Move queue not available" });
            }

            var requestedIds = request.AudiobookIds.Distinct().ToList();
            var audiobooks = await _repo.GetByIdsWithFilesAsync(requestedIds, ct);
            var foundIds = audiobooks.Select(a => a.Id).ToHashSet();
            var missing = requestedIds.Where(id => !foundIds.Contains(id)).ToList();
            if (missing.Count > 0)
            {
                return BadRequest(new
                {
                    message = $"Audiobook id(s) not found: {string.Join(", ", missing)}",
                    missingIds = missing,
                });
            }

            using var scope = _scopeFactory.CreateScope();
            var configService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
            var settings = await configService.GetApplicationSettingsAsync();
            var rootFolders = _rootFolderService != null
                ? await _rootFolderService.GetAllAsync()
                : new List<RootFolder>();

            // Re-compute targets for the selected set so the collision check
            // reflects the live world, not the user's snapshot.
            var targets = new Dictionary<int, string>();
            var result = new OrganizeLibraryApplyResultDto();
            var byKey = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);

            foreach (var audiobook in audiobooks)
            {
                var (target, invalidReason) = ComputeOrganizeTarget(audiobook, settings, rootFolders);
                if (!string.IsNullOrEmpty(invalidReason))
                {
                    result.Skipped++;
                    result.SkippedDetails.Add(new OrganizeApplySkippedDto { AudiobookId = audiobook.Id, Reason = invalidReason });
                    continue;
                }

                var currentKey = NormalizeOrganizeKey(NormalizeOrganizePath(audiobook.BasePath));
                var targetKey = NormalizeOrganizeKey(target);
                if (currentKey == targetKey)
                {
                    result.Skipped++;
                    result.SkippedDetails.Add(new OrganizeApplySkippedDto
                    {
                        AudiobookId = audiobook.Id,
                        Reason = "Already at canonical path",
                    });
                    continue;
                }

                targets[audiobook.Id] = target;
                if (!byKey.TryGetValue(targetKey, out var ids))
                {
                    ids = new List<int>();
                    byKey[targetKey] = ids;
                }
                ids.Add(audiobook.Id);
            }

            // Drop ids that collide with another id in the same request.
            foreach (var (key, ids) in byKey)
            {
                if (ids.Count <= 1) continue;
                foreach (var id in ids)
                {
                    targets.Remove(id);
                    result.Skipped++;
                    result.SkippedDetails.Add(new OrganizeApplySkippedDto
                    {
                        AudiobookId = id,
                        Reason = $"Collides with audiobook id(s) {string.Join(", ", ids.Where(i => i != id))} at the same target",
                    });
                }
                result.Warnings.Add($"Skipped {ids.Count} audiobooks that compute to the same target path '{key}'");
            }

            foreach (var audiobook in audiobooks)
            {
                if (!targets.TryGetValue(audiobook.Id, out var target)) continue;
                try
                {
                    var sourcePath = NormalizeOrganizePath(audiobook.BasePath);
                    var jobId = await _moveQueueService.EnqueueMoveAsync(audiobook.Id, target, sourcePath);
                    result.Queued++;
                    result.QueuedJobs.Add(new OrganizeQueuedJobDto
                    {
                        JobId = jobId.ToString(),
                        AudiobookId = audiobook.Id,
                        AudiobookTitle = audiobook.Title,
                        TargetPath = target,
                    });
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogError(ex, "Failed to enqueue organize move for audiobook {AudiobookId}", audiobook.Id);
                    result.FailedToQueue++;
                    result.SkippedDetails.Add(new OrganizeApplySkippedDto
                    {
                        AudiobookId = audiobook.Id,
                        Reason = $"Failed to enqueue: {ex.Message}",
                    });
                }
            }

            return Ok(result);
        }

        /// <summary>
        /// Build the canonical target folder path for <paramref name="audiobook"/>
        /// using the configured <c>FolderNamingPattern</c>. Returns the
        /// <c>(target, invalidReason)</c> pair — when <c>invalidReason</c> is
        /// non-empty the audiobook should be bucketed as
        /// <see cref="OrganizePreviewStatus.InvalidTarget"/>.
        /// </summary>
        private (string Target, string? InvalidReason) ComputeOrganizeTarget(
            Audiobook audiobook,
            ApplicationSettings settings,
            List<RootFolder> rootFolders)
        {
            var firstAuthor = audiobook.Authors?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
            if (string.IsNullOrWhiteSpace(audiobook.Title))
            {
                return (string.Empty, "Missing title");
            }
            if (string.IsNullOrWhiteSpace(firstAuthor))
            {
                return (string.Empty, "Missing author");
            }
            if (string.IsNullOrWhiteSpace(settings?.FolderNamingPattern))
            {
                return (string.Empty, "FolderNamingPattern is not configured");
            }

            var root = ResolveOrganizeRoot(audiobook.BasePath, settings, rootFolders);
            if (string.IsNullOrEmpty(root))
            {
                return (string.Empty, "Audiobook is outside any configured library root");
            }

            var variables = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
            {
                { "Author", firstAuthor! },
                { "Series", audiobook.Series ?? string.Empty },
                { "Title", audiobook.Title! },
                { "Subtitle", audiobook.Subtitle ?? string.Empty },
                { "Edition", audiobook.Edition ?? string.Empty },
                { "Narrator", audiobook.Narrators != null ? string.Join(", ", audiobook.Narrators.Where(n => !string.IsNullOrWhiteSpace(n))) : string.Empty },
                { "Publisher", audiobook.Publisher ?? string.Empty },
                { "Language", audiobook.Language ?? string.Empty },
                { "Asin", audiobook.Asin ?? string.Empty },
                { "SeriesNumber", audiobook.SeriesNumber ?? string.Empty },
                { "Year", audiobook.PublishYear ?? string.Empty },
                { "Quality", audiobook.Quality ?? string.Empty },
                { "DiskNumber", string.Empty },
                { "ChapterNumber", string.Empty },
            };

            var relative = _fileNamingService.ApplyNamingPattern(settings.FolderNamingPattern, variables, false);
            if (string.IsNullOrWhiteSpace(relative))
            {
                return (string.Empty, "Naming pattern produced an empty path");
            }

            var combined = Path.IsPathRooted(relative) ? relative : Path.Join(root, relative);
            return (NormalizeOrganizePath(combined), null);
        }

        /// <summary>
        /// Pick the canonical root for <paramref name="basePath"/>: the
        /// longest configured root folder (or <c>OutputPath</c>) that contains
        /// the current base path. Falls back to the default root, then to
        /// <c>settings.OutputPath</c>, when the audiobook has no base path
        /// yet. Returns empty when the audiobook lives outside every root.
        /// </summary>
        private static string ResolveOrganizeRoot(
            string? basePath,
            ApplicationSettings settings,
            List<RootFolder> rootFolders)
        {
            var configuredRoots = rootFolders
                .Where(r => !string.IsNullOrWhiteSpace(r.Path))
                .Select(r => NormalizeOrganizePath(r.Path!))
                .ToList();
            if (!string.IsNullOrWhiteSpace(settings.OutputPath))
            {
                var outputNormalized = NormalizeOrganizePath(settings.OutputPath);
                if (!configuredRoots.Any(r => string.Equals(r, outputNormalized, StringComparison.OrdinalIgnoreCase)))
                {
                    configuredRoots.Add(outputNormalized);
                }
            }

            if (string.IsNullOrWhiteSpace(basePath))
            {
                var defaultRoot = rootFolders.FirstOrDefault(r => r.IsDefault)?.Path;
                if (!string.IsNullOrWhiteSpace(defaultRoot)) return NormalizeOrganizePath(defaultRoot);
                if (!string.IsNullOrWhiteSpace(settings.OutputPath)) return NormalizeOrganizePath(settings.OutputPath);
                return configuredRoots.FirstOrDefault() ?? string.Empty;
            }

            var current = NormalizeOrganizePath(basePath);
            return configuredRoots
                .Where(r => string.Equals(r, current, StringComparison.OrdinalIgnoreCase) || FileUtils.IsPathInsideOf(current, r))
                .OrderByDescending(r => r.Length)
                .FirstOrDefault() ?? string.Empty;
        }

        private static string NormalizeOrganizePath(string? path)
            => string.IsNullOrWhiteSpace(path) ? string.Empty : FileUtils.NormalizeStoredPath(path);

        private static string NormalizeOrganizeKey(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            var trimmed = path.TrimEnd('/', '\\');
            return trimmed.ToUpperInvariant();
        }

        /// <summary>
        /// Bulk-update fields (monitored status, quality profile, root folder) for multiple audiobooks at once.
        /// </summary>
        /// <param name="request">Audiobook IDs and the fields to update.</param>
        [HttpPost("bulk-update")]
        public async Task<IActionResult> BulkUpdateAudiobooks([FromBody] BulkUpdateRequest request)
        {
            if (request?.Ids == null || !request.Ids.Any())
            {
                return BadRequest(new { message = "No audiobook IDs provided for bulk update" });
            }

            var results = new List<object>();

            // Fetch application settings once for naming pattern when processing rootFolder changes
            ApplicationSettings? settings = null;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var configService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
                settings = await configService.GetApplicationSettingsAsync();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to load application settings while performing bulk update");
            }

            foreach (var id in request.Ids.Distinct())
            {
                var entryErrors = new List<string>();
                var success = false;

                try
                {
                    var audiobook = await _repo.GetByIdAsync(id);
                    if (audiobook == null)
                    {
                        entryErrors.Add($"Audiobook with ID {id} not found");
                        results.Add(new { id, success, errors = entryErrors });
                        continue;
                    }

                    // Track whether any change was applied
                    var changed = false;

                    // Monitored
                    if (request.Updates != null && request.Updates.TryGetValue("monitored", out var monitoredObj))
                    {
                        try
                        {
                            var monVal = monitoredObj is JsonElement je
                                ? je.ValueKind == JsonValueKind.True
                                : Convert.ToBoolean(monitoredObj);

                            audiobook.Monitored = monVal;
                            changed = true;
                            _logger.LogInformation("Set Monitored={Monitored} for audiobook id={Id}", monVal, id);

                            // History entry
                            await _historyRepository.AddAsync(new History
                            {
                                AudiobookId = audiobook.Id,
                                AudiobookTitle = audiobook.Title ?? "Unknown",
                                EventType = "Updated",
                                Message = $"Monitored set to {monVal}",
                                Source = "BulkUpdate",
                                Timestamp = DateTime.UtcNow
                            });
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            entryErrors.Add($"Invalid monitored value: {ex.Message}");
                        }
                    }

                    // QualityProfileId
                    if (request.Updates != null && request.Updates.TryGetValue("qualityProfileId", out var qpObj))
                    {
                        try
                        {
                            var qpVal = qpObj is JsonElement jq
                                ? jq.GetInt32()
                                : Convert.ToInt32(qpObj);

                            audiobook.QualityProfileId = qpVal;
                            changed = true;
                            _logger.LogInformation("Set QualityProfileId={Profile} for audiobook id={Id}", qpVal, id);

                            await _historyRepository.AddAsync(new History
                            {
                                AudiobookId = audiobook.Id,
                                AudiobookTitle = audiobook.Title ?? "Unknown",
                                EventType = "Updated",
                                Message = $"Quality profile set to {qpVal}",
                                Source = "BulkUpdate",
                                Timestamp = DateTime.UtcNow
                            });
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            entryErrors.Add($"Invalid qualityProfileId value: {ex.Message}");
                        }
                    }

                    // Root folder change (rootFolder => path string)
                    if (request.Updates != null && request.Updates.TryGetValue("rootFolder", out var rootObj))
                    {
                        try
                        {
                            string? rootPath = null;
                            if (rootObj is JsonElement jr)
                            {
                                if (jr.ValueKind == JsonValueKind.String)
                                    rootPath = jr.GetString();
                            }
                            else if (rootObj != null)
                            {
                                rootPath = rootObj.ToString();
                            }

                            if (!string.IsNullOrWhiteSpace(rootPath))
                            {
                                // Use configured naming pattern to compute full base directory for this audiobook
                                var fileNamingPattern = !string.IsNullOrWhiteSpace(settings?.FolderNamingPattern)
                                    ? settings!.FolderNamingPattern
                                    : settings?.FileNamingPattern ?? string.Empty;
                                var newBase = ComputeAudiobookBaseDirectoryFromPattern(audiobook, rootPath, fileNamingPattern);

                                try
                                {
                                    if (!Directory.Exists(newBase))
                                    {
                                        Directory.CreateDirectory(newBase);
                                        _logger.LogInformation("Created directory for audiobook id={Id} at {Path}", id, newBase);
                                    }

                                    audiobook.BasePath = newBase;
                                    changed = true;

                                    await _historyRepository.AddAsync(new History
                                    {
                                        AudiobookId = audiobook.Id,
                                        AudiobookTitle = audiobook.Title ?? "Unknown",
                                        EventType = "Updated",
                                        Message = $"BasePath set to {newBase} via bulk update",
                                        Source = "BulkUpdate",
                                        Timestamp = DateTime.UtcNow
                                    });
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                                {
                                    entryErrors.Add($"Failed to apply root folder for audiobook {id}: {ex.Message}");
                                }
                            }
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            entryErrors.Add($"Invalid rootFolder value: {ex.Message}");
                        }
                    }

                    if (changed)
                    {
                        await _repo.UpdateAsync(audiobook);
                        success = true;
                    }
                    else
                    {
                        entryErrors.Add("No valid updates provided for this audiobook");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    entryErrors.Add($"Unhandled error: {ex.Message}");
                }

                results.Add(new { id, success, errors = entryErrors });
            }

            return Ok(new { message = "Bulk update completed", results });
        }

        /// <summary>
        /// One-shot admin sweep: walk every audiobook and download any external
        /// http(s) cover URL into local library storage. Idempotent — records
        /// already pointing at the local cache are skipped, so re-running after
        /// a successful sweep is a no-op. Per-record failures (download 404, IO
        /// error, etc.) are logged and counted but do not abort the sweep.
        /// </summary>
        [HttpPost("cache-external-covers")]
        public async Task<IActionResult> CacheExternalCovers(CancellationToken cancellationToken)
        {
            if (_externalCoverArtSweepService == null)
            {
                return StatusCode(503, new { message = "External cover-art sweep service is unavailable" });
            }

            ExternalCoverArtSweepResult result;
            try
            {
                result = await _externalCoverArtSweepService.SweepAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return StatusCode(499, new { message = "Sweep cancelled" });
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "External cover-art sweep failed");
                return StatusCode(500, new { message = "External cover-art sweep failed", error = ex.Message });
            }

            return Ok(new
            {
                message = $"Sweep complete: cached {result.Succeeded} of {result.Queued} external covers",
                totalScanned = result.TotalScanned,
                alreadyLocal = result.AlreadyLocal,
                queued = result.Queued,
                succeeded = result.Succeeded,
                failed = result.Failed,
                durationMs = result.DurationMs,
            });
        }

        /// <summary>
        /// Enqueue a force-metadata-refresh scan for every audiobook in the library. The worker
        /// re-extracts file metadata and backfills blank library-level fields (cover, ASIN, ISBN,
        /// series, narrator, etc.) without overwriting existing values. Returns the enqueued job IDs.
        /// </summary>
        [HttpPost("backfill-metadata")]
        public async Task<IActionResult> BackfillMetadata()
        {
            if (_scanQueueService == null)
            {
                return StatusCode(503, new { message = "Scan queue service is unavailable" });
            }

            List<Audiobook> audiobooks;
            try
            {
                audiobooks = (await _repo.GetAllAsync()).ToList();
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Failed to enumerate audiobooks for metadata backfill");
                return StatusCode(500, new { message = "Failed to enumerate audiobooks", error = ex.Message });
            }

            var enqueued = new List<object>();
            foreach (var ab in audiobooks)
            {
                try
                {
                    // skipMissingBasePathCleanup=true keeps a single stale path from cascading
                    // into AudiobookFile deletions during a library-wide sweep.
                    var jobId = await _scanQueueService.EnqueueScanAsync(
                        ab,
                        path: null,
                        forceMetadataRefresh: true,
                        skipMissingBasePathCleanup: true);
                    enqueued.Add(new { audiobookId = ab.Id, jobId });
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to enqueue backfill scan for audiobook {AudiobookId}", ab.Id);
                }
            }

            _logger.LogInformation("Enqueued {Count} metadata-backfill scan jobs across {Total} audiobooks", enqueued.Count, audiobooks.Count);
            return Accepted(new { message = $"Enqueued {enqueued.Count} backfill jobs", total = audiobooks.Count, enqueued });
        }

        /// <summary>
        /// Scan the filesystem for files belonging to this audiobook, extract metadata (ffprobe) and persist AudiobookFile records.
        /// Optional body: { path: "C:\\some\\folder", forceMetadataRefresh: true } to also backfill blank audiobook fields from file tags.
        /// </summary>
        [HttpPost("{id}/scan")]
        public async Task<IActionResult> ScanAudiobookFiles(int id, [FromBody] ScanRequest? request)
        {
            var audiobook = await _repo.GetByIdAsync(id);
            if (audiobook == null) return NotFound(new { message = "Audiobook not found" });

            // If a background scan queue is available, enqueue the job and return Accepted
            if (_scanQueueService != null)
            {
                try
                {
                    var jobId = await _scanQueueService.EnqueueScanAsync(audiobook, request?.Path, request?.ForceMetadataRefresh ?? false);
                    _logger.LogInformation("Enqueued scan job {JobId} for audiobook {AudiobookId} (forceMetadataRefresh: {Force})", jobId, id, request?.ForceMetadataRefresh ?? false);

                    // Broadcast initial job status via SignalR so clients can show queued state
                    try
                    {
                        using var scope = _scopeFactory.CreateScope();
                        var hub = scope.ServiceProvider.GetRequiredService<IHubContext<DownloadHub>>();
                        var job = new { jobId = jobId.ToString(), audiobookId = id, status = "Queued", enqueuedAt = DateTime.UtcNow };
                        await hub.Clients.All.SendAsync("ScanJobUpdate", job);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Failed to broadcast ScanJobUpdate for job {JobId}", jobId);
                    }

                    return Accepted(new { message = "Scan enqueued", jobId });
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogError(ex, "Failed to enqueue scan job for audiobook {AudiobookId}", id);
                    return StatusCode(500, new { message = "Failed to enqueue scan job", error = ex.Message });
                }
            }

            // Determine scan root: request.Path, audiobook.BasePath, or application settings output path
            string? scanRoot = null;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var configService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
                var settings = await configService.GetApplicationSettingsAsync();

                // If audiobook has a BasePath configured, always scan that path for safety
                // Do not fall back to the global output path when a BasePath is present.
                if (!string.IsNullOrEmpty(audiobook.BasePath))
                {
                    scanRoot = Path.GetFullPath(audiobook.BasePath);
                    _logger.LogDebug("Audiobook has BasePath; using it as scan root: {ScanRoot}", LogRedaction.SanitizeFilePath(scanRoot));
                }
                else if (!string.IsNullOrEmpty(request?.Path))
                {
                    // Validate requested path is absolute and contained within a configured root folder or the global output path
                    string requestedFull;
                    try
                    {
                        requestedFull = Path.GetFullPath(request.Path!);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Invalid requested scan path provided: {Path}", LogRedaction.SanitizeFilePath(request.Path));
                        return BadRequest(new { message = "Invalid scan path", path = request.Path });
                    }

                    // Build whitelist of allowed root paths
                    var allowedRoots = new List<string>();
                    if (_rootFolderService != null)
                    {
                        var roots = await _rootFolderService.GetAllAsync();
                        foreach (var r in roots)
                        {
                            try
                            {
                                allowedRoots.Add(Path.GetFullPath(r.Path));
                            }
                            catch (Exception rootPathEx) when (
                                rootPathEx is ArgumentException
                                || rootPathEx is NotSupportedException
                                || rootPathEx is PathTooLongException
                                || rootPathEx is System.Security.SecurityException)
                            {
                                _logger.LogDebug(rootPathEx, "Skipping invalid root folder path during scan allowlist build: {RootPath}", LogRedaction.SanitizeFilePath(r.Path));
                            }
                        }
                    }

                    if (!string.IsNullOrEmpty(settings?.OutputPath))
                    {
                        try
                        {
                            allowedRoots.Add(Path.GetFullPath(settings.OutputPath));
                        }
                        catch (Exception outputPathEx) when (
                            outputPathEx is ArgumentException
                            || outputPathEx is NotSupportedException
                            || outputPathEx is PathTooLongException
                            || outputPathEx is System.Security.SecurityException)
                        {
                            _logger.LogDebug(outputPathEx, "Skipping invalid output path during scan allowlist build: {OutputPath}", settings.OutputPath);
                        }
                    }

                    if (allowedRoots.Count == 0)
                    {
                        _logger.LogWarning("Scan request path provided but no root folders are configured; rejecting request.");
                        return BadRequest(new { message = "No root folders configured; cannot accept explicit scan path" });
                    }

                    // Check that requestedFull is equal to or under one of the allowed roots
                    var allowed = allowedRoots.Any(ar => string.Equals(requestedFull, ar, StringComparison.OrdinalIgnoreCase)
                        || requestedFull.StartsWith(ar.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                        || requestedFull.StartsWith(ar.TrimEnd(Path.AltDirectorySeparatorChar) + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

                    if (!allowed)
                    {
                        _logger.LogWarning("Requested scan path {Path} is not inside configured root folders", LogRedaction.SanitizeFilePath(request.Path));
                        return BadRequest(new { message = "Requested scan path is not within configured root folders", path = request.Path });
                    }

                    scanRoot = requestedFull;
                }
                else
                {
                    // No BasePath and no explicit path - fall back to configured output path
                    scanRoot = !string.IsNullOrEmpty(settings?.OutputPath) ? Path.GetFullPath(settings.OutputPath) : null;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to read application settings for scan; cannot validate request path without configured roots");
                // If BasePath exists prefer it; otherwise, we cannot determine a safe scan root
                if (!string.IsNullOrEmpty(audiobook.BasePath))
                {
                    scanRoot = Path.GetFullPath(audiobook.BasePath);
                }
                else
                {
                    _logger.LogWarning("Configuration unavailable and audiobook has no BasePath; rejecting scan request for audiobook {AudiobookId}", id);
                    return StatusCode(500, new { message = "Failed to determine a safe scan path" });
                }
            }

            if (string.IsNullOrEmpty(scanRoot) || !Directory.Exists(scanRoot))
            {
                return BadRequest(new { message = "Scan path not provided or does not exist", path = scanRoot });
            }

            _logger.LogInformation("Scanning for audiobook files for '{Title}' under: {Path}", LogRedaction.SanitizeText(audiobook.Title), LogRedaction.SanitizeFilePath(scanRoot));

            // Build a simple matching predicate based on title and first author
            var titleToken = (audiobook.Title ?? string.Empty).Replace("\"", string.Empty).Trim();
            var authorToken = audiobook.Authors?.FirstOrDefault() ?? string.Empty;

            var foundFiles = new List<string>();
            try
            {
                // Search recursively but limit to common audio file extensions
                var exts = FileUtils.AudioExtensions;

                // Iterative safe directory traversal to avoid unhandled IO/Access exceptions and handle special characters
                var dirs = new Stack<string>();
                dirs.Push(scanRoot);

                while (dirs.Count > 0)
                {
                    var dir = dirs.Pop();
                    try
                    {
                        var normalizedDir = Path.GetFullPath(dir);

                        foreach (var file in Directory.EnumerateFiles(normalizedDir))
                        {
                            try
                            {
                                var ext = Path.GetExtension(file);
                                if (!exts.Contains(ext, StringComparer.OrdinalIgnoreCase)) continue;
                                var fname = Path.GetFileNameWithoutExtension(file);
                                if (!string.IsNullOrEmpty(titleToken) && fname.IndexOf(titleToken, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    foundFiles.Add(file);
                                    continue;
                                }
                                if (!string.IsNullOrEmpty(authorToken) && file.IndexOf(authorToken, StringComparison.OrdinalIgnoreCase) >= 0)
                                {
                                    foundFiles.Add(file);
                                    continue;
                                }
                            }
                            catch (Exception innerFileEx) when (innerFileEx is not OperationCanceledException && innerFileEx is not OutOfMemoryException && innerFileEx is not StackOverflowException)
                            {
                                _logger.LogDebug(innerFileEx, "Skipped file while scanning {Dir}", normalizedDir);
                                continue;
                            }
                        }

                        foreach (var sub in Directory.EnumerateDirectories(normalizedDir))
                        {
                            dirs.Push(sub);
                        }
                    }
                    catch (System.IO.IOException ioEx)
                    {
                        _logger.LogWarning(ioEx, "IO error while enumerating directory during scan: {Dir}", dir);
                        continue;
                    }
                    catch (UnauthorizedAccessException uaEx)
                    {
                        _logger.LogWarning(uaEx, "Access denied while enumerating directory during scan: {Dir}", dir);
                        continue;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Unexpected error while enumerating directory during scan: {Dir}", dir);
                        continue;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error while scanning filesystem for audiobook files");
                return StatusCode(500, new { message = "Error scanning filesystem", error = ex.Message });
            }

            if (!foundFiles.Any())
            {
                return Ok(new { message = "No files found during scan", scannedPath = scanRoot, found = 0 });
            }

            // Calculate base path for the audiobook files
            var basePath = CalculateBasePath(foundFiles);
            _logger.LogInformation("Calculated base path for audiobook '{Title}': {BasePath}", LogRedaction.SanitizeText(audiobook.Title), LogRedaction.SanitizeFilePath(basePath));

            var created = new List<AudiobookFile>();

            // Extract metadata and persist
            using (var scope = _scopeFactory.CreateScope())
            {
                var metadataService = scope.ServiceProvider.GetRequiredService<IMetadataService>();
                var audioFileRepository = scope.ServiceProvider.GetRequiredService<IAudiobookFileRepository>();
                var historyRepository = scope.ServiceProvider.GetRequiredService<IHistoryRepository>();

                var existingFilesList = await audioFileRepository.GetByAudiobookIdAsync(audiobook.Id);

                foreach (var filePath in foundFiles)
                {
                    try
                    {
                        // Calculate relative path from base path
                        var relativePath = Path.GetRelativePath(basePath, filePath);

                        var existing = existingFilesList.FirstOrDefault(f => f.Path == relativePath);
                        if (existing != null)
                        {
                            _logger.LogInformation("Skipping existing AudiobookFile for audiobook {AudiobookId}: {Path}", audiobook.Id, relativePath);
                            continue;
                        }

                        AudioMetadata? meta = null;
                        try
                        {
                            meta = await metadataService.ExtractFileMetadataAsync(filePath);
                        }
                        catch (Exception mex) when (mex is not OperationCanceledException && mex is not OutOfMemoryException && mex is not StackOverflowException)
                        {
                            _logger.LogWarning(mex, "Failed to extract metadata for file {File}", filePath);
                        }

                        var fi = new FileInfo(filePath);
                        var fileRecord = new AudiobookFile
                        {
                            AudiobookId = audiobook.Id,
                            Path = relativePath, // Store relative path
                            Size = fi.Length,
                            Source = "scan",
                            CreatedAt = DateTime.UtcNow,
                            DurationSeconds = meta?.Duration.TotalSeconds,
                            Format = meta?.Format,
                            Bitrate = meta?.BitRate,
                            SampleRate = meta?.SampleRate,
                            Channels = meta?.Channels
                        };

                        await audioFileRepository.AddAsync(fileRecord);
                        created.Add(fileRecord);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Failed to create AudiobookFile for {File}", filePath);
                    }
                }

                // Update audiobook base path only when we have a non-empty value.
                if (!string.IsNullOrEmpty(basePath))
                {
                    audiobook.BasePath = basePath;
                    await _repo.UpdateAsync(audiobook);
                }

                // Add history entries for newly scanned files
                foreach (var historyEntry in created.Select(fileRecord => new History
                {
                    AudiobookId = audiobook.Id,
                    AudiobookTitle = audiobook.Title ?? "Unknown",
                    EventType = "File Added",
                    Message = $"File scanned and added: {Path.GetFileName(fileRecord.Path)}",
                    Source = "Scan",
                    Data = JsonSerializer.Serialize(new
                    {
                        FilePath = fileRecord.Path,
                        FileSize = fileRecord.Size,
                        Format = fileRecord.Format,
                        Source = fileRecord.Source
                    }),
                    Timestamp = DateTime.UtcNow
                }))
                {
                    await historyRepository.AddAsync(historyEntry);
                }

                // Remove AudiobookFile DB rows for files that no longer exist on disk
                try
                {
                    var allExistingFiles = await audioFileRepository.GetByAudiobookIdAsync(audiobook.Id);

                    var foundSet = new HashSet<string>(foundFiles.Select(f => Path.GetRelativePath(basePath, f)), StringComparer.OrdinalIgnoreCase);
                    var toRemove = allExistingFiles
                        .Where(f => f.Path != null && FileUtils.IsAudioFile(f.Path) && !foundSet.Contains(f.Path))
                        .ToList();

                    List<object> removedFilesDto = new();
                    if (toRemove.Count > 0)
                    {
                        foreach (var rem in toRemove)
                        {
                            try
                            {
                                removedFilesDto.Add(new { id = rem.Id, path = rem.Path });
                                await audioFileRepository.DeleteAsync(rem.Id);
                                _logger.LogInformation("Removing missing AudiobookFile DB row Id={Id} Path={Path}", rem.Id, rem.Path);

                                // Add history entry for removed file
                                var historyEntry = new History
                                {
                                    AudiobookId = audiobook.Id,
                                    AudiobookTitle = audiobook.Title ?? "Unknown",
                                    EventType = "File Removed",
                                    Message = $"File removed (no longer exists): {Path.GetFileName(rem.Path)}",
                                    Source = "Scan",
                                    Data = JsonSerializer.Serialize(new
                                    {
                                        FilePath = rem.Path,
                                        FileSize = rem.Size,
                                        Format = rem.Format,
                                        Source = rem.Source
                                    }),
                                    Timestamp = DateTime.UtcNow
                                };
                                await historyRepository.AddAsync(historyEntry);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                            {
                                _logger.LogWarning(ex, "Failed to remove AudiobookFile Id={Id} Path={Path}", rem.Id, rem.Path);
                            }
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to reconcile audiobook files after scan for audiobook {AudiobookId}", audiobook.Id);
                }

                // Handle legacy filePath field migration
                try
                {
                    var needsUpdate = false;
                    if (!string.IsNullOrEmpty(audiobook.FilePath))
                    {
                        // Check if the legacy filePath exists
                        if (System.IO.File.Exists(audiobook.FilePath))
                        {
                            // File exists - check if we already have an AudiobookFile record for it
                            var existingFileRecord = await audioFileRepository.ExistsAtPathAsync(audiobook.Id, audiobook.FilePath!);

                            if (!existingFileRecord)
                            {
                                // Create AudiobookFile record for the legacy filePath
                                try
                                {
                                    using var afScope = _scopeFactory.CreateScope();
                                    var audioFileService = afScope.ServiceProvider.GetRequiredService<IAudiobookFileService>();
                                    var migrated = await audioFileService.EnsureAudiobookFileAsync(audiobook, audiobook.FilePath, "scan-legacy");
                                    if (migrated)
                                    {
                                        _logger.LogInformation("Migrated legacy filePath to AudiobookFile record for audiobook {AudiobookId}: {Path}", audiobook.Id, audiobook.FilePath);
                                        created.Add(new AudiobookFile { Path = audiobook.FilePath }); // Add to created list for response
                                    }
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                                {
                                    _logger.LogWarning(ex, "Failed to migrate legacy filePath for audiobook {AudiobookId}: {Path}", audiobook.Id, audiobook.FilePath);
                                }
                            }
                        }
                        else
                        {
                            // File doesn't exist - clear the legacy filePath and related fields
                            audiobook.FilePath = null;
                            audiobook.FileSize = null;
                            needsUpdate = true;
                            _logger.LogInformation("Cleared missing legacy filePath for audiobook {AudiobookId}: {Path}", audiobook.Id, audiobook.FilePath);

                            // Add history entry for cleared filePath
                            var historyEntry = new History
                            {
                                AudiobookId = audiobook.Id,
                                AudiobookTitle = audiobook.Title ?? "Unknown",
                                EventType = "File Removed",
                                Message = $"Legacy file path cleared (file no longer exists)",
                                Source = "Scan",
                                Data = JsonSerializer.Serialize(new
                                {
                                    FilePath = audiobook.FilePath,
                                    Source = "legacy-migration"
                                }),
                                Timestamp = DateTime.UtcNow
                            };
                            await historyRepository.AddAsync(historyEntry);
                        }
                    }

                    if (needsUpdate)
                    {
                        await _repo.UpdateAsync(audiobook);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to handle legacy filePath migration for audiobook {AudiobookId}", audiobook.Id);
                }

                // Reload audiobook with files to return
                var updated = await _repo.GetByIdAsync(audiobook.Id);

                // Send "book-available" notification if the audiobook is monitored and files were imported
                if (_notificationService != null && audiobook.Monitored && created.Count > 0)
                {
                    try
                    {
                        using var notificationScope = _scopeFactory.CreateScope();
                        var configService = notificationScope.ServiceProvider.GetRequiredService<IConfigurationService>();
                        var settings = await configService.GetApplicationSettingsAsync();
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
                            filesImported = created.Count,
                            totalFiles = updated?.Files?.Count ?? 0
                        };
                        await _notificationService.SendNotificationAsync("book-available", availableData, settings.WebhookUrl, settings.EnabledNotificationTriggers);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Failed to send book-available notification for audiobook {AudiobookId}", audiobook.Id);
                    }
                }

                return Ok(new { message = "Scan complete", scannedPath = scanRoot, found = foundFiles.Count, created = created.Count, audiobook = updated });
            }
        }

        /// <summary>
        /// Get in-memory scan job status by jobId (debugging/admin helper).
        /// </summary>
        [HttpGet("scan/{jobId}")]
        public IActionResult GetScanJobStatus(string jobId)
        {
            if (_scanQueueService == null) return NotFound(new { message = "Scan queue not available" });
            if (!Guid.TryParse(jobId, out var gid)) return BadRequest(new { message = "Invalid jobId" });
            if (_scanQueueService.TryGetJob(gid, out var job))
            {
                _logger.LogInformation("Queried scan job {JobId} status: {Status}", gid, job!.Status);
                return Ok(job);
            }
            return NotFound(new { message = "Job not found" });
        }

        /// <summary>
        /// Enqueue a background job to move an audiobook's files to a new destination path.
        /// </summary>
        /// <param name="id">Audiobook ID.</param>
        /// <param name="request">Move request with destination path and optional source override.</param>
        /// <returns>Accepted with a job ID that can be polled for progress.</returns>
        [HttpPost("{id}/move")]
        public async Task<IActionResult> EnqueueMove(int id, [FromBody] MoveRequest request)
        {
            if (_moveQueueService == null) return NotFound(new { message = "Move queue not available" });
            var audiobook = await _repo.GetByIdAsync(id);
            if (audiobook == null) return NotFound(new { message = "Audiobook not found" });
            if (request == null) return BadRequest(new { message = "Request body is required" });

            if (string.IsNullOrWhiteSpace(request.DestinationPath))
            {
                return BadRequest(new { message = "DestinationPath is required" });
            }

            try
            {
                // If the path is not rooted, combine with configured output path
                using var scope = _scopeFactory.CreateScope();
                var configService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
                var settings = await configService.GetApplicationSettingsAsync();

                var final = request.DestinationPath!;
                if (!Path.IsPathRooted(final))
                {
                    var root = settings.OutputPath ?? string.Empty;
                    final = Path.Join(root, final.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                }

                // If caller explicitly asked to change the DB without moving files, update the BasePath and return early.
                if (request.MoveFiles == false)
                {
                    try
                    {
                        audiobook.BasePath = final;
                        await _repo.UpdateAsync(audiobook);
                        _logger.LogInformation("Updated BasePath for audiobook {AudiobookId} without moving files: {BasePath}", id, final);
                        return Ok(new { message = "Destination updated" });
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogError(ex, "Failed to update BasePath for audiobook {AudiobookId}", id);
                        return StatusCode(500, new { message = "Failed to update BasePath", error = ex.Message });
                    }
                }

                // Determine source path snapshot to use for the move. Prefer an explicit source from the request
                // (the frontend should send the original source if it updated the audiobook BasePath before requesting a move),
                // otherwise fall back to the current audiobook.BasePath as a best-effort.
                var sourcePath = !string.IsNullOrWhiteSpace(request.SourcePath)
                    ? request.SourcePath
                    : audiobook.BasePath;

                if (string.IsNullOrWhiteSpace(sourcePath))
                {
                    return BadRequest(new { message = "Source path not provided. Supply current source path in the Move request or ensure audiobook has a valid BasePath." });
                }

                // Validate source exists now to provide earlier feedback to clients (avoids enqueueing doomed jobs)
                if (!Directory.Exists(sourcePath))
                {
                    return BadRequest(new { message = "Source path does not exist. Ensure the audiobook's current BasePath exists or provide a valid SourcePath in the request." });
                }

                // Validate target parent is valid and writable (try to create if necessary)
                var targetParent = Path.GetDirectoryName(final);
                if (string.IsNullOrEmpty(targetParent))
                {
                    return BadRequest(new { message = "Invalid target path" });
                }
                try
                {
                    if (!Directory.Exists(targetParent)) Directory.CreateDirectory(targetParent);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to access or create target parent {TargetParent}", targetParent);
                    return BadRequest(new { message = "Target parent path is not writable or unavailable" });
                }

                // If source and target are identical, nothing to do
                try
                {
                    var srcFull = Path.GetFullPath(sourcePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    var tgtFull = Path.GetFullPath(final).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (string.Equals(srcFull, tgtFull, StringComparison.OrdinalIgnoreCase))
                    {
                        return BadRequest(new { message = "Source and target paths are identical; nothing to move." });
                    }
                }
                catch (Exception normalizeEx) when (
                    normalizeEx is ArgumentException
                    || normalizeEx is NotSupportedException
                    || normalizeEx is PathTooLongException
                    || normalizeEx is System.Security.SecurityException)
                {
                    // Ignore errors normalizing paths; background worker will fail if invalid
                    _logger.LogDebug(normalizeEx, "Unable to normalize move paths for audiobook {AudiobookId}", id);
                }

                var jobId = await _moveQueueService.EnqueueMoveAsync(id, final, sourcePath);

                // Broadcast initial job status
                try
                {
                    using var hubScope = _scopeFactory.CreateScope();
                    var hub = hubScope.ServiceProvider.GetRequiredService<IHubContext<DownloadHub>>();
                    var job = new { jobId = jobId.ToString(), audiobookId = id, status = "Queued", enqueuedAt = DateTime.UtcNow };
                    await hub.Clients.All.SendAsync("MoveJobUpdate", job);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to broadcast MoveJobUpdate for job {JobId}", jobId);
                }

                return Accepted(new { message = "Move enqueued", jobId });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Failed to enqueue move job for audiobook {AudiobookId}", id);
                return StatusCode(500, new { message = "Failed to enqueue move job", error = ex.Message });
            }
        }

        /// <summary>
        /// Summary of the persisted MoveJobs table — counts by status plus a
        /// configurable tail of recent completed/failed jobs. Built so the
        /// UI can show "X of Y moves complete (Z failed)" progress for a
        /// long-running organize-library run without spamming the
        /// per-job <c>GET /library/move/{jobId}</c> endpoint, and so the
        /// operator can curl it for post-mortem after a large run.
        ///
        /// Failure messages are returned verbatim — they're the most useful
        /// signal when a batch under-performs. Source/target paths in the
        /// recent-jobs tails are filesystem strings; no extra redaction
        /// because the same data is already in <c>/library/move/{jobId}</c>.
        /// </summary>
        /// <param name="recentLimit">How many of the most-recent completed and most-recent failed jobs to include (default 25, clamped to [0, 200]).</param>
        [HttpGet("move/summary")]
        public async Task<IActionResult> GetMoveQueueSummary([FromQuery] int recentLimit = 25, CancellationToken ct = default)
        {
            if (recentLimit < 0) recentLimit = 0;
            if (recentLimit > 200) recentLimit = 200;

            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ListenArrDbContext>();

            // Single query of the whole table — MoveJobs is small (one row per
            // queued move ever) and the read is a single scan. Saves four
            // round-trips compared to one GroupBy + one OrderBy per status.
            // EF Core's ExecuteUpdate/ExecuteDelete style isn't applicable
            // because we need projections.
            var all = await db.MoveJobs
                .AsNoTracking()
                .Select(j => new
                {
                    j.Id,
                    j.AudiobookId,
                    j.Status,
                    j.Error,
                    j.RequestedPath,
                    j.SourcePath,
                    j.EnqueuedAt,
                    j.UpdatedAt,
                    j.AttemptCount,
                })
                .ToListAsync(ct);

            var byStatus = all
                .GroupBy(j => j.Status ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

            int CountOf(string s) => byStatus.TryGetValue(s, out var n) ? n : 0;

            var recentCompleted = all
                .Where(j => string.Equals(j.Status, "Completed", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(j => j.UpdatedAt ?? j.EnqueuedAt)
                .Take(recentLimit)
                .ToList();
            var recentFailed = all
                .Where(j => string.Equals(j.Status, "Failed", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(j => j.UpdatedAt ?? j.EnqueuedAt)
                .Take(recentLimit)
                .ToList();

            return Ok(new
            {
                total = all.Count,
                queued = CountOf("Queued"),
                processing = CountOf("Processing"),
                completed = CountOf("Completed"),
                failed = CountOf("Failed"),
                // Any status the server hasn't seen the producer use yet
                // (e.g. legacy "Cancelled") falls into a residual bucket so
                // the FE never silently drops a count.
                other = all.Count - CountOf("Queued") - CountOf("Processing") - CountOf("Completed") - CountOf("Failed"),
                recentCompleted,
                recentFailed,
            });
        }

        /// <summary>
        /// Operator escape hatch: flip every <c>Queued</c> or stale
        /// <c>Processing</c> move job older than the given threshold to
        /// <c>Cancelled</c>. The <c>MoveBackgroundService</c>'s consumer
        /// re-checks each job's DB status before processing, so any
        /// cancelled jobs still sitting in the in-memory channel become
        /// no-ops when popped.
        /// </summary>
        /// <param name="olderThanMinutes">Cancel jobs whose last-touched
        /// time (<c>UpdatedAt ?? EnqueuedAt</c>) is older than this many
        /// minutes. Default 5 — longer than the slowest realistic
        /// single-file copy, short enough that truly stuck queues clear
        /// quickly. Use <c>0</c> to cancel everything pending.</param>
        [HttpPost("move/cancel-stale")]
        public async Task<IActionResult> CancelStaleMoveJobs([FromQuery] int olderThanMinutes = 5, CancellationToken ct = default)
        {
            if (_moveQueueService == null) return StatusCode(503, new { message = "Move queue not available" });
            if (olderThanMinutes < 0) olderThanMinutes = 0;
            var cancelled = await _moveQueueService.CancelStalePendingAsync(TimeSpan.FromMinutes(olderThanMinutes), ct);
            return Ok(new { cancelled, olderThanMinutes });
        }

        /// <summary>
        /// Get the current status of a file-move background job.
        /// </summary>
        /// <param name="jobId">The GUID returned when the move was enqueued.</param>
        [HttpGet("move/{jobId}")]
        public IActionResult GetMoveJobStatus(string jobId)
        {
            if (_moveQueueService == null) return NotFound(new { message = "Move queue not available" });
            if (!Guid.TryParse(jobId, out var gid)) return BadRequest(new { message = "Invalid jobId" });
            if (_moveQueueService.TryGetJob(gid, out var job))
            {
                _logger.LogInformation("Queried move job {JobId} status: {Status}", gid, job!.Status);
                return Ok(job);
            }
            return NotFound(new { message = "Job not found" });
        }

        /// <summary>
        /// Re-enqueue a previously failed or completed move job for retry.
        /// </summary>
        /// <param name="jobId">Original move job GUID.</param>
        /// <returns>Accepted with the new job ID.</returns>
        [HttpPost("move/requeue/{jobId}")]
        public async Task<IActionResult> RequeueMoveJob(string jobId)
        {
            if (_moveQueueService == null) return NotFound(new { message = "Move queue not available" });
            if (!Guid.TryParse(jobId, out var gid)) return BadRequest(new { message = "Invalid jobId" });

            var newJobId = await _moveQueueService.RequeueMoveAsync(gid);
            if (newJobId == null)
            {
                return BadRequest(new { message = "Unable to requeue job (not found or invalid status)" });
            }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var hub = scope.ServiceProvider.GetRequiredService<IHubContext<DownloadHub>>();
                var job = new { jobId = newJobId.ToString(), status = "Queued", enqueuedAt = DateTime.UtcNow };
                await hub.Clients.All.SendAsync("MoveJobUpdate", job);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to broadcast MoveJobUpdate for requeued job {JobId}", newJobId);
            }

            return Accepted(new { message = "Requeued move job", jobId = newJobId });
        }

        /// <summary>
        /// Re-enqueue a previously failed or completed scan job for retry.
        /// </summary>
        /// <param name="jobId">Original scan job GUID.</param>
        /// <returns>Accepted with the new job ID.</returns>
        [HttpPost("scan/requeue/{jobId}")]
        public async Task<IActionResult> RequeueScanJob(string jobId)
        {
            if (_scanQueueService == null) return NotFound(new { message = "Scan queue not available" });
            if (!Guid.TryParse(jobId, out var gid)) return BadRequest(new { message = "Invalid jobId" });

            var newJobId = await _scanQueueService.RequeueScanAsync(gid);
            if (newJobId == null)
            {
                return BadRequest(new { message = "Unable to requeue job (not found or invalid status)" });
            }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var hub = scope.ServiceProvider.GetRequiredService<IHubContext<DownloadHub>>();
                var job = new { jobId = newJobId.ToString(), status = "Queued", enqueuedAt = DateTime.UtcNow };
                await hub.Clients.All.SendAsync("ScanJobUpdate", job);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to broadcast ScanJobUpdate for requeued job {JobId}", newJobId);
            }

            return Accepted(new { message = "Requeued scan job", jobId = newJobId });
        }

        private async Task<int> ProcessAudiobookForSearchAsync(
            Audiobook audiobook,
            ISearchService searchService,
            IQualityProfileService qualityProfileService,
            IDownloadService downloadService,
            IDownloadRepository downloadRepository,
            IAudiobookFileRepository audioFileRepository)
        {
            // Check if quality cutoff is already met
            if (await IsQualityCutoffMetAsync(audiobook, qualityProfileService, downloadRepository, audioFileRepository))
            {
                _logger.LogInformation("Quality cutoff already met for audiobook '{Title}', skipping search", LogRedaction.SanitizeText(audiobook.Title));
                return 0;
            }

            // Build search query
            var searchQuery = BuildSearchQuery(audiobook);
            _logger.LogInformation("Searching for audiobook '{Title}' with query: {Query}", LogRedaction.SanitizeText(audiobook.Title), LogRedaction.SanitizeText(searchQuery));

            // Search for results
            var searchResults = await searchService.SearchAsync(searchQuery);
            _logger.LogInformation("Found {Count} raw search results for audiobook '{Title}'", searchResults.Count, LogRedaction.SanitizeText(audiobook.Title));

            // Broadcast raw search result summary for manual-triggered searches (helpful for debugging)
            try
            {
                var rawSummaries = searchResults.Take(10).Select(r => new
                {
                    title = r.Title,
                    asin = r.Asin,
                    source = r.Source,
                    sizeMB = r.Size > 0 ? (r.Size / 1024 / 1024) : -1,
                    seeders = r.Seeders,
                    format = r.Format,
                    downloadType = r.DownloadType
                }).ToList();

                using var scope = _scopeFactory.CreateScope();
                var hub = scope.ServiceProvider.GetRequiredService<IHubContext<DownloadHub>>();
                // Include a structured payload so clients can distinguish manual vs automatic searches
                await hub.Clients.All.SendCoreAsync("SearchProgress", new object[] { new { message = $"Manual search query: {searchQuery}", details = new { rawCount = searchResults.Count, rawSamples = rawSummaries }, type = "interactive", audiobookId = audiobook.Id } });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Failed to broadcast raw search results summary for manual search audiobook {Id}", audiobook.Id);
            }

            if (!searchResults.Any())
            {
                _logger.LogInformation("No search results found for audiobook '{Title}'", LogRedaction.SanitizeText(audiobook.Title));
                return 0;
            }

            // Context-aware filter pass — reject results whose title isn't relevant to
            // this specific audiobook before scoring picks one to download. This is the
            // bulk-search-all endpoint, an auto-pick flow like AutomaticSearchService.
            try
            {
                using var filterScope = _scopeFactory.CreateScope();
                var filterPipeline = filterScope.ServiceProvider.GetRequiredService<Listenarr.Application.Search.Filters.SearchResultFilterPipeline>();
                var preFilterCount = searchResults.Count;
                searchResults = filterPipeline.ApplyFilters(searchResults, logFilteredResults: true, audiobook: audiobook);
                if (searchResults.Count < preFilterCount)
                {
                    _logger.LogInformation("Filtered {Removed} of {Total} raw results for audiobook '{Title}' via context-aware pipeline", preFilterCount - searchResults.Count, preFilterCount, LogRedaction.SanitizeText(audiobook.Title));
                }
                if (!searchResults.Any())
                {
                    _logger.LogInformation("All search results filtered for audiobook '{Title}'", LogRedaction.SanitizeText(audiobook.Title));
                    return 0;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "SearchResultFilterPipeline unavailable; skipping context-aware filtering for audiobook {Id}", audiobook.Id);
            }

            // Score results against quality profile
            var scoredResults = await qualityProfileService.ScoreSearchResults(searchResults, audiobook.QualityProfile!);

            // Log all scored results for debugging
            _logger.LogInformation("Scored {Count} search results for audiobook '{Title}':", scoredResults.Count, LogRedaction.SanitizeText(audiobook.Title));
            foreach (var scoredResult in scoredResults.OrderByDescending(s => s.TotalScore))
            {
                var status = scoredResult.IsRejected ? "REJECTED" : (scoredResult.TotalScore > 0 ? "ACCEPTABLE" : "LOW SCORE");
                _logger.LogInformation("  [{Status}] Score: {Score} | Title: {Title} | Source: {Source} | Size: {Size}MB | Seeders: {Seeders} | Quality: {Quality}",
                    status, scoredResult.TotalScore, LogRedaction.SanitizeText(scoredResult.SearchResult.Title), LogRedaction.SanitizeText(scoredResult.SearchResult.Source),
                    scoredResult.SearchResult.Size / 1024 / 1024, scoredResult.SearchResult.Seeders, scoredResult.SearchResult.Quality);

                if (scoredResult.IsRejected && scoredResult.RejectionReasons.Any())
                {
                    _logger.LogInformation("    Rejection reasons: {Reasons}", string.Join(", ", scoredResult.RejectionReasons));
                }
            }

            var topResult = scoredResults
                .Where(s => !s.IsRejected && s.TotalScore > 0) // Only results that pass quality filters and are not rejected
                .OrderByDescending(s => s.TotalScore)
                .FirstOrDefault(); // Pick only the top scoring result

            if (topResult == null)
            {
                _logger.LogInformation("No acceptable search results found for audiobook '{Title}' after quality filtering", LogRedaction.SanitizeText(audiobook.Title));
                return 0;
            }

            _logger.LogInformation("Found top result for audiobook '{Title}': {ResultTitle} (Score: {Score})",
                LogRedaction.SanitizeText(audiobook.Title), LogRedaction.SanitizeText(topResult.SearchResult.Title), topResult.TotalScore);

            // Add score to the search result for tracking
            topResult.SearchResult.Score = topResult.TotalScore;

            // Queue download for the top result
            var downloadsQueued = 0;
            try
            {
                // Determine appropriate download client for this result
                var isTorrent = IsTorrentResult(topResult.SearchResult);
                var downloadClientId = await GetAppropriateDownloadClientAsync(topResult.SearchResult, isTorrent);

                if (string.IsNullOrEmpty(downloadClientId))
                {
                    _logger.LogWarning("No suitable download client found for result type: {Type}", isTorrent ? "torrent" : "NZB");
                    return 0;
                }

                await downloadService.StartDownloadAsync(topResult.SearchResult, downloadClientId, audiobook.Id);
                downloadsQueued++;

                _logger.LogInformation("Queued download for audiobook '{Title}': {ResultTitle} (Score: {Score})",
                    audiobook.Title, topResult.SearchResult.Title, topResult.TotalScore);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Failed to queue download for audiobook '{Title}': {ResultTitle}",
                    audiobook.Title, topResult.SearchResult.Title);
            }

            return downloadsQueued;
        }

        private async Task<bool> IsQualityCutoffMetAsync(
            Audiobook audiobook,
            IQualityProfileService qualityProfileService,
            IDownloadRepository downloadRepository,
            IAudiobookFileRepository audioFileRepository)
        {
            if (audiobook.QualityProfile == null)
                return false;

            // Get existing downloads for this audiobook
            var existingDownloads = (await downloadRepository.GetByAudiobookIdAsync(audiobook.Id))
                .Where(d => d.Status == DownloadStatus.Completed ||
                            d.Status == DownloadStatus.Downloading ||
                            d.Status == DownloadStatus.ImportPending)
                .ToList();

            // Get existing files for this audiobook
            var existingFiles = await audioFileRepository.GetByAudiobookIdAsync(audiobook.Id);

            if (!existingDownloads.Any() && !existingFiles.Any())
                return false;

            // Check if any existing download meets or exceeds the cutoff quality
            var cutoffQuality = audiobook.QualityProfile.Qualities
                .FirstOrDefault(q => q.Quality == audiobook.QualityProfile.CutoffQuality);

            if (cutoffQuality == null)
                return false;

            // Check downloads first
            foreach (var download in existingDownloads)
            {
                // For completed downloads, check if the file quality meets cutoff
                if (download.Status == DownloadStatus.Completed && !string.IsNullOrEmpty(download.Metadata?.GetValueOrDefault("Quality")?.ToString()))
                {
                    var downloadQuality = download.Metadata["Quality"].ToString();
                    var downloadQualityDefinition = audiobook.QualityProfile.Qualities
                        .FirstOrDefault(q => q.Quality == downloadQuality);

                    if (downloadQualityDefinition != null && downloadQualityDefinition.Priority >= cutoffQuality.Priority)
                    {
                        _logger.LogDebug("Quality cutoff met for audiobook '{Title}' by completed download (Quality: {Quality})",
                            audiobook.Title, downloadQuality);
                        return true;
                    }
                }
                // For active downloads, assume they will meet quality requirements
                else if (download.Status == DownloadStatus.Downloading ||
                         download.Status == DownloadStatus.ImportPending)
                {
                    _logger.LogDebug("Quality cutoff assumed met for audiobook '{Title}' due to active download/import", LogRedaction.SanitizeText(audiobook.Title));
                    return true;
                }
            }

            // Check existing files
            foreach (var file in existingFiles)
            {
                var fileQuality = DetermineFileQuality(file);
                if (!string.IsNullOrEmpty(fileQuality))
                {
                    var fileQualityDefinition = audiobook.QualityProfile.Qualities
                        .FirstOrDefault(q => q.Quality == fileQuality);

                    if (fileQualityDefinition != null && fileQualityDefinition.Priority >= cutoffQuality.Priority)
                    {
                        _logger.LogDebug("Quality cutoff met for audiobook '{Title}' by existing file (Quality: {Quality}, File: {FileName})",
                            audiobook.Title, fileQuality, Path.GetFileName(file.Path));
                        return true;
                    }
                }
            }

            return false;
        }

        private string? DetermineFileQuality(AudiobookFile file)
        {
            // Determine quality based on file properties
            // This mirrors the logic in QualityProfileService.GetQualityScore but works with file metadata

            // Check format/container first
            if (!string.IsNullOrEmpty(file.Container))
            {
                var container = file.Container.ToLower();
                if (container.Contains("flac")) return "FLAC";
                if (container.Contains("m4b") || container.Contains("m4a")) return "M4B";
            }

            if (!string.IsNullOrEmpty(file.Format))
            {
                var format = file.Format.ToLower();
                if (format.Contains("flac")) return "FLAC";
                if (format.Contains("m4b") || format.Contains("m4a")) return "M4B";
                if (format.Contains("aac")) return "M4B"; // AAC in M4B container
            }

            // Check bitrate for MP3 quality determination
            if (file.Bitrate.HasValue)
            {
                var bitrate = file.Bitrate.Value;

                // Convert bits per second to kilobits per second for easier comparison
                var kbps = bitrate / 1000;

                if (kbps >= 320) return "MP3 320kbps";
                if (kbps >= 256) return "MP3 256kbps";
                if (kbps >= 192) return "MP3 192kbps";
                if (kbps >= 128) return "MP3 128kbps";
                if (kbps >= 64) return "MP3 64kbps";

                // For very low bitrates, still classify as MP3
                return "MP3 64kbps";
            }

            // Check codec
            if (!string.IsNullOrEmpty(file.Codec))
            {
                var codec = file.Codec.ToLower();
                if (codec.Contains("flac")) return "FLAC";
                if (codec.Contains("aac")) return "M4B";
                if (codec.Contains("mp3")) return "MP3 128kbps"; // Default MP3 quality if no bitrate info
                if (codec.Contains("opus")) return "M4B"; // Opus is often in M4B containers
            }

            // If we can't determine quality from metadata, try to infer from file extension
            if (!string.IsNullOrEmpty(file.Path))
            {
                var extension = Path.GetExtension(file.Path).ToLower();
                switch (extension)
                {
                    case ".flac":
                        return "FLAC";
                    case ".m4b":
                    case ".m4a":
                        return "M4B";
                    case ".mp3":
                        return "MP3 128kbps"; // Conservative default for MP3
                    case ".aac":
                        return "M4B";
                    case ".opus":
                        return "M4B";
                }
            }

            return null; // Unable to determine quality
        }

        private string BuildSearchQuery(Audiobook audiobook)
        {
            var parts = new List<string>();

            // Add title
            if (!string.IsNullOrEmpty(audiobook.Title))
                parts.Add(audiobook.Title);

            // Add primary author
            if (audiobook.Authors != null && audiobook.Authors.Any())
                parts.Add(audiobook.Authors.First());

            // Add series if available
            if (!string.IsNullOrEmpty(audiobook.Series))
                parts.Add(audiobook.Series);

            return string.Join(" ", parts);
        }

        private bool IsTorrentResult(SearchResult result)
        {
            // Check DownloadType first if it's set
            if (!string.IsNullOrEmpty(result.DownloadType))
            {
                if (result.DownloadType == "DDL")
                {
                    return false; // DDL is not a torrent
                }
                else if (result.DownloadType == "Torrent")
                {
                    return true;
                }
                else if (result.DownloadType == "Usenet")
                {
                    return false;
                }
            }

            // Fallback to legacy detection logic
            // Check for NZB first - if it has an NZB URL, it's a Usenet/NZB download
            if (!string.IsNullOrEmpty(result.NzbUrl))
            {
                return false;
            }

            // Check for torrent indicators - magnet link or torrent file
            if (!string.IsNullOrEmpty(result.MagnetLink) || !string.IsNullOrEmpty(result.TorrentUrl))
            {
                return true;
            }

            // If neither is set, we can't reliably determine the type
            // Log a warning and default to false (NZB) as a safer choice
            _logger.LogWarning("Unable to determine result type for '{Title}' from source '{Source}'. No MagnetLink, TorrentUrl, or NzbUrl found. Defaulting to NZB.",
                result.Title, result.Source);
            return false;
        }

        private async Task<string> GetAppropriateDownloadClientAsync(SearchResult searchResult, bool isTorrent)
        {
            using var scope = _scopeFactory.CreateScope();
            var configurationService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();

            // Special handling for DDL downloads - they don't use external clients
            if (searchResult.DownloadType?.Equals("DDL", StringComparison.OrdinalIgnoreCase) == true)
            {
                _logger.LogInformation("DDL download detected, using internal DDL client");
                return "DDL";
            }

            // Get all configured download clients
            var clients = await configurationService.GetDownloadClientConfigurationsAsync();
            var enabledClients = clients.Where(c => c.IsEnabled).ToList();

            _logger.LogInformation("Looking for {ClientType} client. Found {Count} enabled download clients: {Clients}",
                isTorrent ? "torrent" : "NZB",
                enabledClients.Count,
                string.Join(", ", enabledClients.Select(c => $"{c.Name} ({c.Type})")));

            if (isTorrent)
            {
                // Prefer qBittorrent, then Transmission
                var client = enabledClients.FirstOrDefault(c => c.Type.Equals("qbittorrent", StringComparison.OrdinalIgnoreCase))
                          ?? enabledClients.FirstOrDefault(c => c.Type.Equals("transmission", StringComparison.OrdinalIgnoreCase));

                if (client != null)
                {
                    _logger.LogInformation("Selected torrent client: {ClientName} ({ClientType})", client.Name, client.Type);
                }
                else
                {
                    _logger.LogWarning("No torrent client (qBittorrent or Transmission) found among enabled clients");
                }

                return client?.Id ?? string.Empty;
            }
            else
            {
                // Prefer SABnzbd, then NZBGet
                var client = enabledClients.FirstOrDefault(c => c.Type.Equals("sabnzbd", StringComparison.OrdinalIgnoreCase))
                          ?? enabledClients.FirstOrDefault(c => c.Type.Equals("nzbget", StringComparison.OrdinalIgnoreCase));

                if (client != null)
                {
                    _logger.LogInformation("Selected NZB client: {ClientName} ({ClientType})", client.Name, client.Type);
                }
                else
                {
                    _logger.LogWarning("No NZB client (SABnzbd or NZBGet) found among enabled clients");
                }

                return client?.Id ?? string.Empty;
            }
        }

        // Helper to convert incoming update values (possibly JsonElement or boxed types) to the target property type
        private static object? ConvertUpdateValue(object? value, Type targetType)
        {
            if (value == null)
            {
                if (targetType == typeof(string)) return string.Empty;
                if (targetType.IsValueType) return Activator.CreateInstance(targetType);
                return null;
            }

            // Unwrap JsonElement if present (from System.Text.Json)
            if (value is JsonElement je)
            {
                try
                {
                    if (je.ValueKind == JsonValueKind.Number && (targetType == typeof(int) || targetType == typeof(int?)))
                        return je.GetInt32();
                    if (je.ValueKind == JsonValueKind.Number && targetType == typeof(double))
                        return je.GetDouble();
                    if (je.ValueKind == JsonValueKind.True || je.ValueKind == JsonValueKind.False)
                        return je.GetBoolean();
                    if (je.ValueKind == JsonValueKind.String)
                        return je.GetString();
                    // Fall back to raw string
                    return je.GetRawText();
                }
                catch (Exception jsonElementConvertEx) when (
                    jsonElementConvertEx is InvalidOperationException
                    || jsonElementConvertEx is FormatException
                    || jsonElementConvertEx is OverflowException)
                {
                    // continue to other conversion attempts
                    System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                }
            }

            // Handle nullable types
            var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

            // Enums
            if (underlying.IsEnum)
            {
                if (value is string s)
                    return Enum.Parse(underlying, s, true);
                return Enum.ToObject(underlying, Convert.ChangeType(value, Enum.GetUnderlyingType(underlying)));
            }

            // If value already matches
            if (underlying.IsInstanceOfType(value))
                return value;

            // Try Convert.ChangeType on primitives
            try
            {
                return Convert.ChangeType(value, underlying);
            }
            catch (Exception changeTypeEx) when (
                changeTypeEx is InvalidCastException
                || changeTypeEx is FormatException
                || changeTypeEx is OverflowException
                || changeTypeEx is ArgumentException)
            {
                // Final fallback: attempt parse from string
                var str = value.ToString();
                if (underlying == typeof(int) && int.TryParse(str, out var i)) return i;
                if (underlying == typeof(double) && double.TryParse(str, out var d)) return d;
                if (underlying == typeof(bool) && bool.TryParse(str, out var b)) return b;
                if (underlying == typeof(string)) return str;
            }

            // As a last resort, return the original value
            return value;
        }

        private string ComputeAudiobookBaseDirectoryFromPattern(Audiobook audiobook, string rootPath, string fileNamingPattern)
        {
            // Derive directory pattern from the user's file naming pattern
            // Remove file-specific tokens like DiskNumber and ChapterNumber to create a directory structure
            string directoryPattern;
            if (!string.IsNullOrWhiteSpace(fileNamingPattern))
            {
                // Remove file-specific patterns and create a directory pattern
                directoryPattern = fileNamingPattern;

                // Remove file-specific tokens that don't make sense for directories
                directoryPattern = Regex.Replace(directoryPattern, @"\{DiskNumber[^}]*\}", "", RegexOptions.IgnoreCase);
                directoryPattern = Regex.Replace(directoryPattern, @"\{ChapterNumber[^}]*\}", "", RegexOptions.IgnoreCase);

                // Clean up any resulting double separators or empty parts
                directoryPattern = Regex.Replace(directoryPattern, @"[\\/]\s*[\\/]", "/");
                directoryPattern = Regex.Replace(directoryPattern, @"^\s*[\\/]", "");
                directoryPattern = Regex.Replace(directoryPattern, @"[\\/]\s*$", "");

                // If the pattern is now empty or doesn't contain directory separators, use a fallback
                if (string.IsNullOrWhiteSpace(directoryPattern) || !directoryPattern.Contains("/"))
                {
                    directoryPattern = "{Author}/{Title}";
                }
            }
            else
            {
                // Fallback to default directory pattern
                directoryPattern = "{Author}/{Title}";
            }

            // For series books, ensure we include the series in the directory structure
            if (!string.IsNullOrWhiteSpace(audiobook.Series) && !directoryPattern.Contains("{Series}"))
            {
                // Insert series between author and title if not already present
                if (directoryPattern.Contains("{Author}/{Title}"))
                {
                    directoryPattern = directoryPattern.Replace("{Author}/{Title}", "{Author}/{Series}/{Title}");
                }
                else if (directoryPattern.Contains("{Author}/"))
                {
                    directoryPattern = directoryPattern.Replace("{Author}/", "{Author}/{Series}/");
                }
            }

            // If the audiobook has no Series, remove any {Series} tokens from the directory pattern
            // Tests expect the controller to strip the Series token when series metadata is missing.
            if (string.IsNullOrWhiteSpace(audiobook.Series))
            {
                directoryPattern = Regex.Replace(directoryPattern, @"\{Series[^}]*\}", string.Empty, RegexOptions.IgnoreCase);
                // Clean up any resulting duplicate separators or empty parts again
                directoryPattern = Regex.Replace(directoryPattern, @"[\\/]\s*[\\/]", "/");
                directoryPattern = Regex.Replace(directoryPattern, @"^\s*[\\/]", "");
                directoryPattern = Regex.Replace(directoryPattern, @"[\\/]\s*$", "");
            }

            // Build variables for naming pattern using audiobook-level metadata
            var variables = new Dictionary<string, object>
            {
                { "Author", SanitizeDirectoryName(audiobook.Authors?.FirstOrDefault() ?? "Unknown Author") },
                { "Series", SanitizeDirectoryName(!string.IsNullOrWhiteSpace(audiobook.Series) ? audiobook.Series! : string.Empty) },
                { "Title", SanitizeDirectoryName(audiobook.Title ?? "Unknown Title") },
                { "Subtitle", SanitizeDirectoryName(audiobook.Subtitle ?? string.Empty) },
                { "Edition", SanitizeDirectoryName(audiobook.Edition ?? string.Empty) },
                { "Narrator", SanitizeDirectoryName((audiobook.Narrators != null && audiobook.Narrators.Any()) ? string.Join(", ", audiobook.Narrators.Where(n => !string.IsNullOrWhiteSpace(n))) : string.Empty) },
                { "Publisher", SanitizeDirectoryName(audiobook.Publisher ?? string.Empty) },
                { "Language", SanitizeDirectoryName(audiobook.Language ?? string.Empty) },
                { "Asin", SanitizeDirectoryName(audiobook.Asin ?? string.Empty) },
                { "SeriesNumber", audiobook.SeriesNumber ?? string.Empty },
                { "Year", audiobook.PublishYear ?? string.Empty },
                { "Quality", string.Empty },
                { "DiskNumber", string.Empty },
                { "ChapterNumber", string.Empty }
            };

            // Apply the directory pattern to get the relative directory path
            var relative = _fileNamingService.ApplyNamingPattern(directoryPattern, variables, false);

            // Combine with root path
            var combined = ResolvePathWithOptionalBase(rootPath, relative);

            return combined;
        }

        private string CalculateBasePath(List<string> filePaths)
        {
            if (!filePaths.Any())
                return string.Empty;

            // Convert all paths to directory paths (get parent directory for each file)
            var directories = filePaths
                .Select(p => FileUtils.NormalizeStoredPath(Path.GetDirectoryName(p) ?? p))
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (directories.Count == 1)
            {
                // All files are in the same directory
                return directories[0];
            }

            // Find the common ancestor directory where there are no longer <=1 things stored
            var commonPath = GetCommonPath(directories);

            // Walk up the directory tree until we find a directory that has more than 1 subdirectory or file
            var currentPath = commonPath;
            while (!string.IsNullOrEmpty(currentPath))
            {
                try
                {
                    var parent = Directory.GetParent(currentPath)?.FullName;
                    if (string.IsNullOrEmpty(parent))
                        break;

                    // Count subdirectories and files in parent
                    var subDirs = Directory.GetDirectories(parent).Length;
                    var files = Directory.GetFiles(parent).Length;

                    // If parent has more than 1 thing (subdirs + files), we've found our base path
                    if (subDirs + files > 1)
                    {
                        return currentPath;
                    }

                    currentPath = parent;
                }
                catch (Exception traversalEx) when (
                    traversalEx is IOException
                    || traversalEx is UnauthorizedAccessException
                    || traversalEx is System.Security.SecurityException
                    || traversalEx is ArgumentException
                    || traversalEx is NotSupportedException)
                {
                    // If we can't access the directory, stop here
                    _logger.LogDebug(traversalEx, "Stopping common-base-path ascent at {Path} due to traversal error", currentPath);
                    break;
                }
            }

            return commonPath;
        }

        private string GetCommonPath(List<string> paths)
        {
            if (!paths.Any())
                return string.Empty;

            var firstPath = FileUtils.NormalizeStoredPath(paths[0]);
            var commonPath = firstPath;

            foreach (var path in paths.Skip(1).Select(rawPath => FileUtils.NormalizeStoredPath(rawPath)))
            {
                var minLength = Math.Min(commonPath.Length, path.Length);
                var commonLength = 0;

                for (int i = 0; i < minLength; i++)
                {
                    if (commonPath[i] == path[i])
                        commonLength++;
                    else
                        break;
                }

                // Ensure we don't break in the middle of a directory name
                if (commonLength < commonPath.Length)
                    commonLength = commonPath.LastIndexOf(Path.DirectorySeparatorChar, commonLength - 1) is var lastSep && lastSep >= 0
                        ? lastSep + 1
                        : 0;

                commonPath = commonPath.Substring(0, commonLength);

                if (string.IsNullOrEmpty(commonPath))
                    break;
            }

            // Ensure it's a valid directory path
            if (!string.IsNullOrEmpty(commonPath) && !Directory.Exists(commonPath))
            {
                var parent = Directory.GetParent(commonPath)?.FullName;
                return parent ?? commonPath;
            }

            return commonPath;
        }

        private string SanitizeDirectoryName(string name)
        {
            // Remove or replace characters that are invalid in directory names
            var invalidChars = Path.GetInvalidFileNameChars();
            foreach (var c in invalidChars)
            {
                name = name.Replace(c, '_');
            }

            // Also replace some additional characters that might cause issues
            name = name.Replace(":", "_").Replace("*", "_").Replace("?", "_").Replace("\"", "_").Replace("<", "_").Replace(">", "_").Replace("|", "_");

            // Trim whitespace and return
            return name.Trim();
        }

        private static string ComputeShortHash(string? input)
        {
            if (string.IsNullOrEmpty(input))
                return Guid.NewGuid().ToString("N").Substring(0, 12);

            var bytes = Encoding.UTF8.GetBytes(input);
            var hash = SHA1.HashData(bytes);
            // Return first 16 hex characters for a compact identifier
            return BitConverter.ToString(hash).Replace("-", "").Substring(0, 16).ToLowerInvariant();
        }

        public sealed class AudiobookIdentifierWriteItem
        {
            public AudiobookExternalIdentifierType Type { get; set; }
            public string Value { get; set; } = string.Empty;
            public string? Region { get; set; }
            public bool IsPrimary { get; set; }
            public AudiobookExternalIdentifierSource? Source { get; set; }
        }

        public sealed class ReplaceAudiobookIdentifiersRequest
        {
            public List<AudiobookIdentifierWriteItem> Identifiers { get; set; } = new();
        }

        public sealed class AudiobookIdentifierResponseItem
        {
            public int Id { get; set; }
            public AudiobookExternalIdentifierType Type { get; set; }
            public string Value { get; set; } = string.Empty;
            public string ValueNormalized { get; set; } = string.Empty;
            public string? Region { get; set; }
            public bool IsPrimary { get; set; }
            public AudiobookExternalIdentifierSource Source { get; set; }
            public DateTime CreatedAt { get; set; }
            public DateTime UpdatedAt { get; set; }
        }

        private static AudiobookIdentifierResponseItem ToIdentifierResponse(AudiobookExternalIdentifier identifier)
        {
            return new AudiobookIdentifierResponseItem
            {
                Id = identifier.Id,
                Type = identifier.Type,
                Value = string.IsNullOrWhiteSpace(identifier.ValueRaw) ? identifier.ValueNormalized : identifier.ValueRaw,
                ValueNormalized = identifier.ValueNormalized,
                Region = identifier.Region,
                IsPrimary = identifier.IsPrimary,
                Source = identifier.Source,
                CreatedAt = identifier.CreatedAt,
                UpdatedAt = identifier.UpdatedAt
            };
        }

        private static List<AudiobookExternalIdentifier> OrderIdentifiers(IEnumerable<AudiobookExternalIdentifier>? identifiers)
        {
            return (identifiers ?? Enumerable.Empty<AudiobookExternalIdentifier>())
                .OrderBy(i => i.Type)
                .ThenByDescending(i => i.IsPrimary)
                .ThenBy(i => i.Source)
                .ThenBy(i => i.ValueNormalized)
                .ToList();
        }

        private static List<AudiobookExternalIdentifier> BuildLegacyBackfillIdentifiers(Audiobook audiobook, AudiobookExternalIdentifierSource source)
        {
            var now = DateTime.UtcNow;
            var result = new List<AudiobookExternalIdentifier>();

            if (!string.IsNullOrWhiteSpace(audiobook.Asin) &&
                AudiobookIdentifierNormalizer.TryNormalize(AudiobookExternalIdentifierType.Asin, audiobook.Asin, out var normalizedAsin, out _))
            {
                result.Add(new AudiobookExternalIdentifier
                {
                    Type = AudiobookExternalIdentifierType.Asin,
                    ValueRaw = AudiobookIdentifierNormalizer.NormalizeRawValueForStorage(audiobook.Asin),
                    ValueNormalized = normalizedAsin,
                    Region = null,
                    IsPrimary = true,
                    Source = source,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }

            var seenIsbns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var isbn in audiobook.Isbn ?? new List<string>())
            {
                if (!AudiobookIdentifierNormalizer.TryNormalize(AudiobookExternalIdentifierType.Isbn, isbn, out var normalizedIsbn, out _))
                {
                    continue;
                }

                if (!seenIsbns.Add(normalizedIsbn)) continue;

                result.Add(new AudiobookExternalIdentifier
                {
                    Type = AudiobookExternalIdentifierType.Isbn,
                    ValueRaw = AudiobookIdentifierNormalizer.NormalizeRawValueForStorage(isbn),
                    ValueNormalized = normalizedIsbn,
                    Region = null,
                    IsPrimary = false,
                    Source = source,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }

            if (!string.IsNullOrWhiteSpace(audiobook.OpenLibraryId) &&
                AudiobookIdentifierNormalizer.TryNormalize(AudiobookExternalIdentifierType.OpenLibraryId, audiobook.OpenLibraryId, out var normalizedOlid, out _))
            {
                result.Add(new AudiobookExternalIdentifier
                {
                    Type = AudiobookExternalIdentifierType.OpenLibraryId,
                    ValueRaw = AudiobookIdentifierNormalizer.NormalizeRawValueForStorage(audiobook.OpenLibraryId),
                    ValueNormalized = normalizedOlid,
                    Region = null,
                    IsPrimary = true,
                    Source = source,
                    CreatedAt = now,
                    UpdatedAt = now
                });
            }

            return result;
        }

        private static string IdentifierTypeValueKey(AudiobookExternalIdentifier item)
        {
            return $"{item.Type}|{item.ValueNormalized}";
        }

        private static string IdentifierFullKey(AudiobookExternalIdentifier item)
        {
            return $"{item.Type}|{item.ValueNormalized}|{item.Region ?? string.Empty}";
        }

        private static string IdentifierFullSourceKey(AudiobookExternalIdentifier item)
        {
            return IdentifierFullSourceKey(item.Type, item.ValueNormalized, item.Region, item.Source);
        }

        private static string IdentifierFullSourceKey(
            AudiobookExternalIdentifierType type,
            string? valueNormalized,
            string? region,
            AudiobookExternalIdentifierSource source)
        {
            return $"{type}|{valueNormalized ?? string.Empty}|{region ?? string.Empty}|{source}";
        }

        private static List<AudiobookExternalIdentifier> GetEffectiveIdentifiers(Audiobook audiobook)
        {
            var merged = new List<AudiobookExternalIdentifier>();
            var seenFull = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenTypeValue = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddIfNew(AudiobookExternalIdentifier item)
            {
                if (string.IsNullOrWhiteSpace(item.ValueNormalized)) return;

                var typeValueKey = IdentifierTypeValueKey(item);
                if (item.Source == AudiobookExternalIdentifierSource.Imported && seenTypeValue.Contains(typeValueKey))
                {
                    // Imported identifiers are compatibility aliases; suppress them when a canonical
                    // identifier with the same normalized value already exists (even if region differs).
                    return;
                }

                var fullKey = IdentifierFullKey(item);
                if (!seenFull.Add(fullKey)) return;
                merged.Add(item);
                seenTypeValue.Add(typeValueKey);
            }

            foreach (var existing in (audiobook.ExternalIdentifiers ?? new List<AudiobookExternalIdentifier>())
                .OrderBy(i => i.Type)
                .ThenByDescending(i => i.IsPrimary)
                .ThenBy(i => i.Source == AudiobookExternalIdentifierSource.Imported ? 1 : 0)
                .ThenBy(i => i.Source)
                .ThenBy(i => i.ValueNormalized))
            {
                AddIfNew(existing);
            }

            foreach (var legacy in BuildLegacyBackfillIdentifiers(audiobook, AudiobookExternalIdentifierSource.Imported))
            {
                AddIfNew(legacy);
            }

            return OrderIdentifiers(merged);
        }

        private static void SyncLegacyFieldsFromIdentifiers(Audiobook audiobook)
        {
            var identifiers = OrderIdentifiers(audiobook.ExternalIdentifiers);

            var primaryAsin = identifiers
                .Where(i => i.Type == AudiobookExternalIdentifierType.Asin)
                .OrderByDescending(i => i.IsPrimary)
                .ThenBy(i => i.Source)
                .FirstOrDefault();
            audiobook.Asin = primaryAsin?.ValueNormalized;

            audiobook.Isbn = identifiers
                .Where(i => i.Type == AudiobookExternalIdentifierType.Isbn)
                .Select(i => i.ValueNormalized)
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var primaryOlid = identifiers
                .Where(i => i.Type == AudiobookExternalIdentifierType.OpenLibraryId)
                .OrderByDescending(i => i.IsPrimary)
                .ThenBy(i => i.Source)
                .FirstOrDefault();
            audiobook.OpenLibraryId = primaryOlid?.ValueNormalized;
        }

        private static void SyncImportedIdentifiersFromLegacyFields(Audiobook audiobook)
        {
            audiobook.ExternalIdentifiers ??= new List<AudiobookExternalIdentifier>();

            audiobook.ExternalIdentifiers = audiobook.ExternalIdentifiers
                .Where(i => i.Source != AudiobookExternalIdentifierSource.Imported)
                .ToList();

            var existingTypeValueKeys = new HashSet<string>(
                audiobook.ExternalIdentifiers
                    .Where(i => !string.IsNullOrWhiteSpace(i.ValueNormalized))
                    .Select(IdentifierTypeValueKey),
                StringComparer.OrdinalIgnoreCase);
            var seenImportedFullKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var imported = BuildLegacyBackfillIdentifiers(audiobook, AudiobookExternalIdentifierSource.Imported);
            foreach (var item in imported.Where(item =>
                         !string.IsNullOrWhiteSpace(item.ValueNormalized) &&
                         !existingTypeValueKeys.Contains(IdentifierTypeValueKey(item)) &&
                         seenImportedFullKeys.Add(IdentifierFullKey(item))))
            {
                audiobook.ExternalIdentifiers.Add(item);
            }
        }

        private static IEnumerable<string> EnumerateMetadataRescanRegions(string? preferredRegion)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var ordered = new List<string>();
            void AddOrdered(string? region)
            {
                var normalized = AudiobookIdentifierNormalizer.NormalizeRegion(region);
                if (string.IsNullOrWhiteSpace(normalized)) return;
                if (seen.Add(normalized)) ordered.Add(normalized);
            }

            AddOrdered(preferredRegion);
            AddOrdered("us");
            AddOrdered("uk");

            if (ordered.Count == 0)
            {
                ordered.Add("us");
            }

            return ordered;
        }

        private static bool TryExtractMetadataLookupResult(
            object? rawResult,
            out AudibleBookResponse? metadata,
            out string? source)
        {
            metadata = null;
            source = null;
            if (rawResult == null) return false;

            if (rawResult is AudibleBookResponse direct)
            {
                metadata = direct;
                return true;
            }

            var type = rawResult.GetType();
            var metadataProp = type.GetProperty("metadata", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (metadataProp != null)
            {
                var metadataValue = metadataProp.GetValue(rawResult);
                if (metadataValue is AudibleBookResponse audible)
                {
                    metadata = audible;
                }
                else if (metadataValue is JsonElement metadataElement && metadataElement.ValueKind == JsonValueKind.Object)
                {
                    try
                    {
                        metadata = metadataElement.Deserialize<AudibleBookResponse>();
                    }
                    catch (JsonException)
                    {
                        metadata = null;
                    }
                    catch (NotSupportedException)
                    {
                        metadata = null;
                    }
                }
            }

            var sourceProp = type.GetProperty("source", BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (sourceProp != null)
            {
                source = sourceProp.GetValue(rawResult)?.ToString();
            }

            return metadata != null;
        }

        private static bool ApplyMetadataRescanPatch(Audiobook audiobook, AudibleBookMetadata metadata)
        {
            var legacyIdentifierFieldsTouched = false;

            if (!string.IsNullOrWhiteSpace(metadata.Title)) audiobook.Title = metadata.Title;
            if (!string.IsNullOrWhiteSpace(metadata.Subtitle)) audiobook.Subtitle = metadata.Subtitle;
            if (!string.IsNullOrWhiteSpace(metadata.PublishYear)) audiobook.PublishYear = metadata.PublishYear;
            if (!string.IsNullOrWhiteSpace(metadata.PublishedDate)) audiobook.PublishedDate = metadata.PublishedDate;
            if (!string.IsNullOrWhiteSpace(metadata.Description)) audiobook.Description = metadata.Description;
            if (!string.IsNullOrWhiteSpace(metadata.Publisher)) audiobook.Publisher = metadata.Publisher;
            if (!string.IsNullOrWhiteSpace(metadata.Language)) audiobook.Language = metadata.Language;
            if (metadata.Runtime.HasValue && metadata.Runtime.Value > 0) audiobook.Runtime = metadata.Runtime;
            if (!string.IsNullOrWhiteSpace(metadata.Version)) audiobook.Version = metadata.Version;

            if ((metadata.SeriesMemberships != null && metadata.SeriesMemberships.Any()) ||
                !string.IsNullOrWhiteSpace(metadata.Series) ||
                !string.IsNullOrWhiteSpace(metadata.SeriesNumber))
            {
                AudiobookSeriesMembershipHelper.ApplyToAudiobook(
                    audiobook,
                    metadata.SeriesMemberships,
                    metadata.Series,
                    metadata.SeriesNumber);
            }

            var authors = NormalizeMetadataStringList(
                (metadata.Authors != null && metadata.Authors.Any())
                    ? metadata.Authors
                    : (!string.IsNullOrWhiteSpace(metadata.Author) ? new List<string> { metadata.Author! } : null));
            if (authors.Count > 0) audiobook.Authors = authors;

            var narrators = NormalizeMetadataStringList(
                (metadata.Narrators != null && metadata.Narrators.Any())
                    ? metadata.Narrators
                    : (!string.IsNullOrWhiteSpace(metadata.Narrator) ? new List<string> { metadata.Narrator! } : null));
            if (narrators.Count > 0) audiobook.Narrators = narrators;

            var genres = NormalizeMetadataStringList(metadata.Genres);
            if (genres.Count > 0) audiobook.Genres = genres;

            var isbns = NormalizeMetadataStringList(metadata.Isbn);
            if (isbns.Count > 0)
            {
                audiobook.Isbn = isbns;
                legacyIdentifierFieldsTouched = true;
            }

            if (!string.IsNullOrWhiteSpace(metadata.Asin))
            {
                audiobook.Asin = metadata.Asin;
                legacyIdentifierFieldsTouched = true;
            }

            if (!string.IsNullOrWhiteSpace(metadata.OpenLibraryId))
            {
                audiobook.OpenLibraryId = metadata.OpenLibraryId;
                legacyIdentifierFieldsTouched = true;
            }

            return legacyIdentifierFieldsTouched;
        }

        private Task<string?> MoveMetadataImageToLibraryStorageAsync(Audiobook audiobook, string imageUrl)
            => LibraryImageStorageHelper.MoveExternalImageToLibraryAsync(_imageCacheService, audiobook, imageUrl, _logger);

        private static List<string> NormalizeMetadataStringList(IEnumerable<string>? values)
        {
            if (values == null) return new List<string>();

            return values
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static string? FirstNonEmpty(params string?[] values)
        {
            var first = values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
            return first?.Trim();
        }

        private static bool TryConsumeMetadataRescanQuota(
            IMemoryCache cache,
            Microsoft.AspNetCore.Http.HttpContext? httpContext,
            int audiobookId,
            out string message,
            out int retryAfterSeconds)
        {
            message = string.Empty;
            retryAfterSeconds = 0;

            var actorKey = BuildMetadataRescanActorKey(httpContext);
            var cacheKey = $"metadata-rescan-rate:{audiobookId}:{actorKey}";
            var now = DateTime.UtcNow;

            if (!cache.TryGetValue(cacheKey, out MetadataRescanRateLimitState? state) || state == null)
            {
                state = new MetadataRescanRateLimitState
                {
                    WindowStartUtc = now,
                    Count = 0,
                    LastAttemptUtc = null
                };
            }

            if (state.LastAttemptUtc.HasValue)
            {
                var cooldownRemaining = TimeSpan.FromSeconds(MetadataRescanCooldownSeconds) - (now - state.LastAttemptUtc.Value);
                if (cooldownRemaining > TimeSpan.Zero)
                {
                    retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(cooldownRemaining.TotalSeconds));
                    message = $"Rescan cooldown active. Please wait {retryAfterSeconds} seconds before rescanning this audiobook again.";
                    return false;
                }
            }

            if ((now - state.WindowStartUtc) >= TimeSpan.FromMinutes(MetadataRescanWindowMinutes))
            {
                state.WindowStartUtc = now;
                state.Count = 0;
            }

            if (state.Count >= MetadataRescanMaxRequestsPerWindow)
            {
                var windowEndsAt = state.WindowStartUtc.AddMinutes(MetadataRescanWindowMinutes);
                var remaining = windowEndsAt - now;
                retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
                message = $"Metadata rescan rate limit reached for this audiobook. Try again in {retryAfterSeconds} seconds.";
                return false;
            }

            state.Count++;
            state.LastAttemptUtc = now;

            cache.Set(
                cacheKey,
                state,
                new MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(MetadataRescanWindowMinutes + 5)
                });

            return true;
        }

        private static string BuildMetadataRescanActorKey(Microsoft.AspNetCore.Http.HttpContext? httpContext)
        {
            var user = httpContext?.User;
            var userId =
                user?.FindFirst("sub")?.Value ??
                user?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ??
                user?.Identity?.Name;

            var remoteIp = httpContext?.Connection?.RemoteIpAddress?.ToString() ?? "unknown";

            var actorDescriptor = !string.IsNullOrWhiteSpace(userId)
                ? $"user:{userId}|ip:{remoteIp}"
                : $"ip:{remoteIp}";

            return ComputeShortHash(actorDescriptor);
        }

        private sealed class MetadataRescanRateLimitState
        {
            public DateTime WindowStartUtc { get; set; }
            public int Count { get; set; }
            public DateTime? LastAttemptUtc { get; set; }
        }

        public class BulkDeleteRequest
        {
            public List<int> Ids { get; set; } = new List<int>();
        }

        public class BulkUpdateRequest
        {
            public List<int> Ids { get; set; } = new List<int>();
            public Dictionary<string, object> Updates { get; set; } = new Dictionary<string, object>();
        }

        [HttpPost("rename/preview")]
        public async Task<IActionResult> PreviewRename([FromBody] BulkRenameRequest request, CancellationToken ct)
        {
            if (_renameService == null)
            {
                return StatusCode(503, new { message = "Rename service not available" });
            }

            if (request?.AudiobookIds == null || request.AudiobookIds.Length == 0)
            {
                return BadRequest(new { message = "At least one audiobook ID is required" });
            }

            if (request.AudiobookIds.Length > 500)
            {
                return BadRequest(new { message = "Cannot preview more than 500 audiobooks at once" });
            }

            var previews = await _renameService.PreviewRenameAsync(request.AudiobookIds, ct);
            return Ok(previews);
        }

        [HttpPost("rename")]
        public async Task<IActionResult> ExecuteRename([FromBody] ExecuteRenameRequest request, CancellationToken ct)
        {
            if (_renameService == null)
            {
                return StatusCode(503, new { message = "Rename service not available" });
            }

            if (request?.Operations == null || request.Operations.Count == 0)
            {
                return BadRequest(new { message = "At least one rename operation is required" });
            }

            if (request.Operations.Count > 500)
            {
                return BadRequest(new { message = "Cannot execute more than 500 rename operations at once" });
            }

            var results = await _renameService.ExecuteRenameAsync(request.Operations, ct);
            return Ok(results);
        }

        [HttpPost("{id}/rename/preview")]
        public async Task<IActionResult> PreviewRenameSingle(int id, CancellationToken ct)
        {
            if (_renameService == null)
            {
                return StatusCode(503, new { message = "Rename service not available" });
            }

            var previews = await _renameService.PreviewRenameAsync(new[] { id }, ct);
            var preview = previews.FirstOrDefault();
            if (preview == null)
            {
                return NotFound(new { message = "Audiobook not found" });
            }

            return Ok(preview);
        }

        [HttpPost("{id}/rename")]
        public async Task<IActionResult> ExecuteRenameSingle(int id, [FromBody] RenameOperation operation, CancellationToken ct)
        {
            if (_renameService == null)
            {
                return StatusCode(503, new { message = "Rename service not available" });
            }

            if (operation == null)
            {
                return BadRequest(new { message = "Rename operation is required" });
            }

            operation.AudiobookId = id;
            var results = await _renameService.ExecuteRenameAsync(new List<RenameOperation> { operation }, ct);
            var result = results.FirstOrDefault();
            if (result == null)
            {
                return NotFound(new { message = "Audiobook not found" });
            }

            return Ok(result);
        }

        /// <summary>
        /// Read the embedded container tags (ffprobe) for a single tracked file. Used by the
        /// "extract file to another audiobook" UI to seed an Audible candidate search and to
        /// display what the file actually claims to be alongside Audible's metadata.
        /// </summary>
        [HttpGet("{audiobookId}/files/{fileId}/embedded-metadata")]
        public async Task<IActionResult> GetEmbeddedFileMetadata(int audiobookId, int fileId, CancellationToken ct)
        {
            if (_fileExtractionService == null)
            {
                return StatusCode(503, new { message = "File extraction service not available" });
            }

            var embedded = await _fileExtractionService.ReadEmbeddedAsync(audiobookId, fileId, ct);
            if (embedded == null)
            {
                return NotFound(new { message = "File not found." });
            }

            return Ok(embedded);
        }

        /// <summary>
        /// Move a single tracked file out of its current audiobook and onto a destination
        /// audiobook constructed from the supplied Audible metadata. If an audiobook with
        /// the same ASIN already exists, the request must include a <c>duplicateStrategy</c>
        /// of "merge" or "duplicate"; otherwise a 409 is returned with the existing
        /// audiobook details so the caller can re-ask the user.
        /// </summary>
        [HttpPost("{audiobookId}/files/{fileId}/extract")]
        public async Task<IActionResult> ExtractFileToNewAudiobook(
            int audiobookId,
            int fileId,
            [FromBody] ExtractFileRequest request,
            CancellationToken ct)
        {
            if (_fileExtractionService == null)
            {
                return StatusCode(503, new { message = "File extraction service not available" });
            }

            if (request == null)
            {
                return BadRequest(new { message = "Extract request body is required." });
            }

            if (request.Metadata == null || string.IsNullOrWhiteSpace(request.Metadata.Title))
            {
                return BadRequest(new { message = "Destination metadata must include a title." });
            }

            var result = await _fileExtractionService.ExtractToNewAudiobookAsync(audiobookId, fileId, request, ct);
            if (result.Success)
            {
                return Ok(result);
            }

            if (result.Conflict != null && request.DuplicateStrategy == DuplicateStrategy.None)
            {
                return Conflict(result);
            }

            return BadRequest(result);
        }

        public class AddToLibraryRequest
        {
            public AudibleBookMetadata Metadata { get; set; } = new();
            public bool Monitored { get; set; } = true;
            public int? QualityProfileId { get; set; }
            public bool AutoSearch { get; set; } = false;
            // Optional destination override for placing the audiobook base directory
            public string? DestinationPath { get; set; }
            public SearchResult? SearchResult { get; set; }
        }

        public class PreviewPathRequest
        {
            public AudibleBookMetadata Metadata { get; set; } = new();
            public string? DestinationRoot { get; set; }
        }

        public class MoveRequest
        {
            public string? DestinationPath { get; set; }
            public string? SourcePath { get; set; }
            // If provided and false, update DB only and do not enqueue a move job
            public bool? MoveFiles { get; set; }
            // When moving files, whether to delete the original folder if empty after the move
            public bool? DeleteEmptySource { get; set; }
        }

    }
}



