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

using Listenarr.Application.Notifications.Progress;
using Listenarr.Application.Search;
using Listenarr.Application.Search.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.HostedServices.Search
{
    public class AutomaticSearchService(
        ILogger<AutomaticSearchService> logger,
        IAutomaticSearchProcessor processor,
        IWorkerCycleRunner cycleRunner,
        IServiceScopeFactory scopeFactory) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation("AutomaticSearchService started. Sweep interval and per-book cooldown are read from settings each cycle");

            await cycleRunner.RunPeriodicAsync(
                nameof(AutomaticSearchService),
                initialDelay: TimeSpan.FromMinutes(5),
                intervalProvider: GetSweepInterval,
                runCycle: processor.RunCycleAsync,
                stoppingToken);

            logger.LogInformation("AutomaticSearchService stopped");
        }

        // Read each cycle so a settings change takes effect without a restart. The sweep
        // cadence only decides how often the worker looks; eligibility is the cooldown's job.
        private TimeSpan GetSweepInterval()
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var configurationService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
                var settings = configurationService.GetApplicationSettingsAsync().GetAwaiter().GetResult();
                return AutomaticSearchTiming.FromSettings(settings).SweepInterval;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "Could not read automatic-search timing settings; sweeping every {Hours} hour(s)", AutomaticSearchTiming.Default.SweepInterval.TotalHours);
                return AutomaticSearchTiming.Default.SweepInterval;
            }
        }
    }

    public partial class AutomaticSearchProcessor : IAutomaticSearchProcessor, IAutomaticSearchInvoker
    {
        private readonly ILogger<AutomaticSearchProcessor> _logger;
        private readonly IServiceScopeFactory _serviceScopeFactory;
        private readonly AutomaticSearchResultClassifier _resultClassifier;
        private readonly AutomaticSearchQualityEvaluator _qualityEvaluator;
        private readonly AutomaticSearchDownloadClientSelector _downloadClientSelector;

        public AutomaticSearchProcessor(
            ILogger<AutomaticSearchProcessor> logger,
            IServiceScopeFactory serviceScopeFactory)
        {
            _logger = logger;
            _serviceScopeFactory = serviceScopeFactory;
            _resultClassifier = new AutomaticSearchResultClassifier(_logger);
            _qualityEvaluator = new AutomaticSearchQualityEvaluator(_logger);
            _downloadClientSelector = new AutomaticSearchDownloadClientSelector(_serviceScopeFactory, _logger);
        }

        public Task RunCycleAsync(CancellationToken cancellationToken) => PerformAutomaticSearchesAsync(cancellationToken);


        private async Task PerformAutomaticSearchesAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Starting automatic search cycle for monitored audiobooks");

            using var scope = _serviceScopeFactory.CreateScope();
            var audiobookRepository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            var downloadRepository = scope.ServiceProvider.GetRequiredService<IDownloadRepository>();
            var fileRepository = scope.ServiceProvider.GetRequiredService<IAudiobookFileRepository>();
            var searchService = scope.ServiceProvider.GetRequiredService<ISearchService>();
            var qualityProfileService = scope.ServiceProvider.GetRequiredService<IQualityProfileService>();
            var downloadService = scope.ServiceProvider.GetRequiredService<IDownloadService>();
            var configurationService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
            // Optional: drives the live search-activity indicator (sidebar).
            var searchProgressReporter = scope.ServiceProvider.GetService<SearchProgressReporter>();

            var timing = AutomaticSearchTiming.FromSettings(await configurationService.GetApplicationSettingsAsync());

            // A book is eligible once per cooldown, however often the sweep itself runs.
            var cutoffTime = timing.EligibilityCutoff(DateTime.UtcNow);
            var monitoredAudiobooks = await audiobookRepository.GetMonitoredAudiobooksForSearchAsync(cutoffTime, stoppingToken);

            _logger.LogInformation("Found {Count} monitored audiobooks eligible for automatic search", monitoredAudiobooks.Count);

            if (!monitoredAudiobooks.Any())
            {
                _logger.LogInformation("No audiobooks require automatic search at this time");
                if (searchProgressReporter != null)
                {
                    await searchProgressReporter.BroadcastAutomaticAsync("Search idle", "idle", addToFeed: false);
                }
                return;
            }

            var processedCount = 0;
            var downloadsQueued = 0;

            var isFirstBook = true;
            foreach (var audiobook in monitoredAudiobooks)
            {
                if (stoppingToken.IsCancellationRequested)
                    break;

                try
                {
                    // Pause between books so a large wanted list does not hammer the indexers.
                    if (!isFirstBook && timing.BookDelay > TimeSpan.Zero)
                    {
                        await Task.Delay(timing.BookDelay, stoppingToken);
                    }
                    isFirstBook = false;

                    var downloadsQueuedForBook = await ProcessAudiobookAsync(
                        audiobook, searchService, qualityProfileService, downloadService, audiobookRepository, downloadRepository, fileRepository, stoppingToken,
                        blockedReleaseRepository: scope.ServiceProvider.GetService<IBlockedReleaseRepository>(),
                        filterPipeline: scope.ServiceProvider.GetService<SearchResultFilterPipeline>());

                    downloadsQueued += downloadsQueuedForBook;
                    processedCount++;

                    // Per-book outcome for the live activity indicator. Best-effort —
                    // never let a notification failure interrupt the sweep.
                    if (searchProgressReporter != null)
                    {
                        var (outcomeMessage, outcomeStage) = downloadsQueuedForBook > 0
                            ? ($"Grabbed {downloadsQueuedForBook} release(s) for {audiobook.Title}", "grabbed")
                            : ($"No results for {audiobook.Title}", "no_results");
                        await searchProgressReporter.BroadcastAutomaticAsync(outcomeMessage, outcomeStage, audiobook.Id, audiobook.Asin);
                    }

                    // Update last search time — TARGETED write only. This loop walks
                    // a snapshot loaded at cycle start, sometimes for a long time;
                    // saving the whole stale entity here clobbered every concurrent
                    // change to the row (live case: a verification reset reverted
                    // minutes after the user made it).
                    audiobook.LastSearchTime = DateTime.UtcNow;
                    await audiobookRepository.SetLastSearchTimeAsync(audiobook.Id, audiobook.LastSearchTime.Value);

                    _logger.LogInformation("Processed audiobook '{Title}' - queued {QueuedCount} downloads",
                        audiobook.Title, downloadsQueuedForBook);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException ex)
                {
                    _logger.LogWarning(ex, "Automatic search processing canceled/timed out for audiobook '{Title}' (ID: {Id})", audiobook.Title, audiobook.Id);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogError(ex, "Error processing audiobook '{Title}' (ID: {Id})", audiobook.Title, audiobook.Id);
                }
            }

            _logger.LogInformation("Automatic search cycle completed. Processed {ProcessedCount} audiobooks, queued {DownloadsQueued} total downloads",
                processedCount, downloadsQueued);

            // Sweep finished — the live indicator returns to idle until the next cycle.
            if (searchProgressReporter != null)
            {
                await searchProgressReporter.BroadcastAutomaticAsync(
                    $"Search idle — last sweep processed {processedCount} book(s)", "idle", addToFeed: false);
            }
        }

        private async Task<int> ProcessAudiobookAsync(
            Audiobook audiobook,
            ISearchService searchService,
            IQualityProfileService qualityProfileService,
            IDownloadService downloadService,
            IAudiobookRepository audiobookRepository,
            IDownloadRepository downloadRepository,
            IAudiobookFileRepository fileRepository,
            CancellationToken stoppingToken,
            IBlockedReleaseRepository? blockedReleaseRepository = null,
            SearchResultFilterPipeline? filterPipeline = null)
        {
            if (audiobook.QualityProfile == null)
            {
                _logger.LogWarning("Audiobook '{Title}' has no quality profile assigned", audiobook.Title);
                return 0;
            }

            // Check if there's already an active download for this audiobook
            var allDownloads = await downloadRepository.GetByAudiobookIdAsync(audiobook.Id, stoppingToken);
            var activeDownload = allDownloads.FirstOrDefault(d =>
                d.Status == DownloadStatus.Queued ||
                d.Status == DownloadStatus.Downloading ||
                d.Status == DownloadStatus.Paused ||
                d.Status == DownloadStatus.Processing ||
                d.Status == DownloadStatus.Ready ||
                d.Status == DownloadStatus.ImportPending);

            if (activeDownload != null)
            {
                _logger.LogInformation("Audiobook '{Title}' already has an active download (ID: {DownloadId}, Status: {Status}), skipping automatic search",
                    audiobook.Title, activeDownload.Id, activeDownload.Status);
                return 0;
            }

            // Check existing quality and decide whether to search
            var (cutoffMet, bestExistingQuality) = await _qualityEvaluator.GetExistingQualityAsync(audiobook, downloadRepository, fileRepository, stoppingToken);
            _logger.LogInformation("Audiobook '{Title}': cutoff met={CutoffMet}, best existing quality={BestQuality}",
                audiobook.Title, cutoffMet, bestExistingQuality ?? "none");

            // Skip automatic search if quality cutoff is already met
            if (cutoffMet)
            {
                _logger.LogInformation("Quality cutoff already met for audiobook '{Title}', skipping automatic search", audiobook.Title);
                return 0;
            }

            // Build search query
            var searchQuery = _resultClassifier.BuildSearchQuery(audiobook);
            _logger.LogInformation("Searching for audiobook '{Title}' with query: {Query}", audiobook.Title, searchQuery);

            // Search for results
            // Restrict to the Newznab "Books > Audiobook" category (3030) so music-only
            // indexers configured without a category can't answer audiobook queries at all.
            var searchResults = await searchService.SearchAsync(searchQuery, category: "3030", isAutomaticSearch: true);
            _logger.LogInformation("Found {Count} raw search results for audiobook '{Title}'", searchResults.Count, audiobook.Title);

            // Author-free fallback: release names often omit the author, so a full title+author
            // query can return nothing even when the book is available. Retry without the author,
            // then constrain the broadened results to titles that still match this book, so a
            // same-title/different-book release can't pass quality scoring and be grabbed.
            if (searchResults.Count == 0)
            {
                var fallbackQuery = _resultClassifier.BuildTitleFallbackSearchQuery(audiobook);
                if (!string.IsNullOrWhiteSpace(fallbackQuery)
                    && !string.Equals(fallbackQuery, searchQuery, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("No results for '{Title}' with the full query; retrying without the author: {Query}",
                        audiobook.Title, fallbackQuery);
                    var fallbackResults = await searchService.SearchAsync(fallbackQuery, isAutomaticSearch: true);
                    searchResults = _resultClassifier.ConstrainFallbackResultsToTitle(fallbackResults, audiobook);
                    _logger.LogInformation("Author-free fallback for '{Title}': {Raw} raw result(s), {Kept} kept after title match",
                        audiobook.Title, fallbackResults.Count, searchResults.Count);
                }
            }

            // Broadcast detailed debug info about the raw search results to help diagnose automatic search failures
            try
            {
                // Build a concise summary of up to 10 raw results
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

                using var scope = _serviceScopeFactory.CreateScope();
                var hub = scope.ServiceProvider.GetRequiredService<IHubContext<DownloadHub>>();
                // Send structured payload with type and audiobookId so the UI can ignore automatic messages by default
                await hub.Clients.All.SendCoreAsync("SearchProgress", new object[] { new { message = $"Automatic search query: {searchQuery}", details = new { rawCount = searchResults.Count, rawSamples = rawSummaries }, type = "automatic", audiobookId = audiobook.Id } });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Failed to broadcast raw search results summary for audiobook {Id}", audiobook.Id);
            }

            if (!searchResults.Any())
            {
                _logger.LogInformation("No search results found for audiobook '{Title}'", audiobook.Title);
                return 0;
            }

            // Context-aware filter pass — reject non-audiobook matter and results whose
            // title isn't relevant to THIS audiobook (e.g., a Patterson Hood concert
            // recording answering a James Patterson query on one shared surname)
            // before scoring can pick one on seeders/quality alone.
            if (filterPipeline != null)
            {
                var preFilterCount = searchResults.Count;
                searchResults = filterPipeline.ApplyFilters(searchResults, logFilteredResults: true, audiobook: audiobook);
                if (searchResults.Count < preFilterCount)
                {
                    _logger.LogInformation(
                        "Filtered {Removed} of {Total} raw results for audiobook '{Title}' via context-aware pipeline",
                        preFilterCount - searchResults.Count, preFilterCount, audiobook.Title);
                }
                if (!searchResults.Any())
                {
                    _logger.LogInformation("No relevant results remain for audiobook '{Title}' after filtering", audiobook.Title);
                    return 0;
                }
            }

            // Drop releases the user explicitly rejected for this book ("Wrong
            // content") — without this the same wrong-content/collection release
            // wins the search again immediately after every cleanup.
            if (blockedReleaseRepository != null)
            {
                try
                {
                    var blocked = await blockedReleaseRepository.GetByAudiobookIdAsync(audiobook.Id, stoppingToken);
                    if (blocked.Count > 0)
                    {
                        var before = searchResults.Count;
                        searchResults = searchResults
                            .Where(r => !BlockedReleaseMatcher.IsBlocked(r.Title, r.MagnetLink, blocked))
                            .ToList();
                        if (searchResults.Count < before)
                        {
                            _logger.LogInformation(
                                "Filtered {Removed} blocked release(s) for audiobook '{Title}'",
                                before - searchResults.Count, audiobook.Title);
                        }
                        if (!searchResults.Any())
                        {
                            _logger.LogInformation("All remaining results are blocked releases for audiobook '{Title}'", audiobook.Title);
                            return 0;
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to apply blocked-release filter for audiobook {Id}", audiobook.Id);
                }
            }

            // Score results against quality profile (runtime enables the
            // collection-sized-release rejection).
            var scoredResults = await qualityProfileService.ScoreSearchResults(searchResults, audiobook.QualityProfile, audiobook.Runtime);

            // Log all scored results for debugging
            _logger.LogInformation("Scored {Count} search results for audiobook '{Title}':", scoredResults.Count, audiobook.Title);

            // Broadcast scored result summaries (score + rejection reasons) to aid debugging
            try
            {
                var scoredSummaries = scoredResults.Select(s => new
                {
                    title = s.SearchResult.Title,
                    asin = s.SearchResult.Asin,
                    totalScore = s.TotalScore,
                    isRejected = s.IsRejected,
                    rejectionReasons = s.RejectionReasons,
                    source = s.SearchResult.Source,
                    sizeMB = s.SearchResult.Size > 0 ? (s.SearchResult.Size / 1024 / 1024) : -1,
                    seeders = s.SearchResult.Seeders,
                    format = s.SearchResult.Format
                }).ToList();

                using var scope2 = _serviceScopeFactory.CreateScope();
                var hub2 = scope2.ServiceProvider.GetRequiredService<IHubContext<DownloadHub>>();
                await hub2.Clients.All.SendCoreAsync("SearchProgress", new object[] { new { message = $"Scored results for '{audiobook.Title}'", details = new { scoredCount = scoredResults.Count, scoredSamples = scoredSummaries }, type = "automatic", audiobookId = audiobook.Id } });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Failed to broadcast scored search results for audiobook {Id}", audiobook.Id);
            }
            foreach (var scoredResult in scoredResults.OrderByDescending(s => s.TotalScore))
            {
                var status = scoredResult.IsRejected ? "REJECTED" : (scoredResult.TotalScore > 0 ? "ACCEPTABLE" : "LOW SCORE");
                _logger.LogInformation("  [{Status}] Score: {Score} | Title: {Title} | Source: {Source} | Size: {Size}MB | Seeders: {Seeders} | Quality: {Quality}",
                    status, scoredResult.TotalScore, scoredResult.SearchResult.Title, scoredResult.SearchResult.Source,
                    scoredResult.SearchResult.Size / 1024 / 1024, scoredResult.SearchResult.Seeders, scoredResult.SearchResult.Quality);

                if (scoredResult.IsRejected && scoredResult.RejectionReasons.Any())
                {
                    _logger.LogInformation("    Rejection reasons: {Reasons}", string.Join(", ", scoredResult.RejectionReasons));
                }
            }

            var acceptable = scoredResults
                .Where(s => !s.IsRejected) // Only non-rejected results
                .OrderByDescending(s => s.TotalScore)
                .ToList();

            if (acceptable.Count == 0)
            {
                _logger.LogInformation("No acceptable search results found for audiobook '{Title}' after quality filtering", audiobook.Title);
                return 0;
            }

            // AI release gate — the same gate the per-book search-and-download path runs.
            // This path never consulted it and grabbed "White Rural Rage: The Threat to
            // American Democracy" for James Patterson's "American Rage" (title-only fallback).
            var topResult = await PickThroughAiGateAsync(audiobook, acceptable, stoppingToken);
            if (topResult == null)
            {
                _logger.LogWarning("AI release gate flagged every acceptable candidate for audiobook '{Title}' — skipping this cycle", audiobook.Title);
                return 0;
            }

            _logger.LogInformation("Found top result for audiobook '{Title}': {ResultTitle} (Score: {Score}, Quality: {Quality})",
                audiobook.Title, topResult.SearchResult.Title, topResult.TotalScore, topResult.SearchResult.Quality);

            // Check if the found result is better quality than what we already have
            if (!string.IsNullOrEmpty(bestExistingQuality))
            {
                var resultIsBetter = _qualityEvaluator.IsQualityBetter(topResult.SearchResult.Quality, bestExistingQuality, audiobook.QualityProfile);
                if (!resultIsBetter)
                {
                    _logger.LogInformation("Top result quality '{ResultQuality}' is not better than existing quality '{ExistingQuality}' for audiobook '{Title}', skipping download",
                        topResult.SearchResult.Quality, bestExistingQuality, audiobook.Title);
                    return 0;
                }
                _logger.LogInformation("Top result quality '{ResultQuality}' is better than existing quality '{ExistingQuality}', proceeding with download",
                    topResult.SearchResult.Quality, bestExistingQuality);
            }
            else
            {
                _logger.LogInformation("No existing files for audiobook '{Title}', proceeding with download", audiobook.Title);
            }

            // Add score to the search result for tracking
            topResult.SearchResult.Score = topResult.TotalScore;

            // Queue download for the top result
            var downloadsQueued = 0;
            try
            {
                // Determine appropriate download client for this result
                var isTorrent = _resultClassifier.IsTorrentResult(topResult.SearchResult);
                var downloadClientId = await _downloadClientSelector.GetAppropriateDownloadClientAsync(topResult.SearchResult, isTorrent);

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


        private async Task<QualityScore?> PickThroughAiGateAsync(Audiobook audiobook, List<QualityScore> acceptable, CancellationToken ct)
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var aiAssist = scope.ServiceProvider.GetService<IAiAssistService>();
            var configuration = scope.ServiceProvider.GetService<Listenarr.Application.Configuration.Contracts.IConfigurationService>();
            if (aiAssist == null || configuration == null)
            {
                return acceptable[0];
            }

            return await new AiReleaseGate(aiAssist, configuration, _logger).PickAsync(audiobook, acceptable, ct);
        }

    }
}
