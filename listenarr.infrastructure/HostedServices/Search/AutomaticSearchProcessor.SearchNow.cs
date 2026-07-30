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
using Listenarr.Application.Search.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.HostedServices.Search
{
    // Per-book search-now entry (manual/not-audiobook/dashboard callers), split out
    // of AutomaticSearchService.cs to keep both under the architecture size cap.
    public partial class AutomaticSearchProcessor
    {
        /// <summary>
        /// Manual per-book "search now" — same pipeline as the sweep, invoked
        /// on demand (e.g. right after wrong content was purged). Blocked
        /// releases still apply: the user rejected that exact release's CONTENT.
        /// </summary>
        public async Task<AutomaticSearchBookResult> SearchAudiobookNowAsync(int audiobookId, CancellationToken ct = default)
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var audiobookRepository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            var downloadRepository = scope.ServiceProvider.GetRequiredService<IDownloadRepository>();
            var fileRepository = scope.ServiceProvider.GetRequiredService<IAudiobookFileRepository>();
            var searchService = scope.ServiceProvider.GetRequiredService<ISearchService>();
            var qualityProfileService = scope.ServiceProvider.GetRequiredService<IQualityProfileService>();
            var downloadService = scope.ServiceProvider.GetRequiredService<IDownloadService>();
            var configService = scope.ServiceProvider.GetRequiredService<Listenarr.Application.Configuration.Contracts.IConfigurationService>();

            var audiobook = await audiobookRepository.GetByIdAsync(audiobookId);
            if (audiobook == null)
            {
                return new AutomaticSearchBookResult
                {
                    AudiobookId = audiobookId,
                    Success = false,
                    Message = "Audiobook not found"
                };
            }

            try
            {
                var appSettings = await configService.GetApplicationSettingsAsync();
                var queued = await ProcessAudiobookAsync(
                    audiobook, searchService, qualityProfileService, downloadService,
                    audiobookRepository, downloadRepository, fileRepository,
                    appSettings.AutomaticSearchTitleOnlyFallback, ct,
                    blockedReleaseRepository: scope.ServiceProvider.GetService<IBlockedReleaseRepository>(),
                    filterPipeline: scope.ServiceProvider.GetService<SearchResultFilterPipeline>());

                // Targeted write for the same reason as the cycle loop: never
                // re-save the whole entity just to bump the timestamp.
                audiobook.LastSearchTime = DateTime.UtcNow;
                await audiobookRepository.SetLastSearchTimeAsync(audiobook.Id, audiobook.LastSearchTime.Value);

                // Close the live-activity loop the same way the sweep does.
                // The indexer fan-out broadcast a transient "Searching …" for
                // this book; without a terminal outcome + idle here, the
                // sidebar pill showed that search as running until the NEXT
                // sweep — hours later (live case: a 16:28 "Search now" pinned
                // "Searching 3 indexers…" on screen overnight).
                var reporter = scope.ServiceProvider.GetService<Listenarr.Application.Notifications.Progress.SearchProgressReporter>();
                if (reporter != null)
                {
                    var (outcomeMessage, outcomeStage) = queued > 0
                        ? ($"Grabbed {queued} release(s) for {audiobook.Title}", "grabbed")
                        : ($"No results for {audiobook.Title}", "no_results");
                    await reporter.BroadcastAutomaticAsync(outcomeMessage, outcomeStage, audiobook.Id, audiobook.Asin);
                    await reporter.BroadcastAutomaticAsync("Search idle", "idle", addToFeed: false);
                }

                return new AutomaticSearchBookResult
                {
                    AudiobookId = audiobookId,
                    Title = audiobook.Title ?? string.Empty,
                    Success = true,
                    DownloadsQueued = queued
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Manual automatic-search failed for audiobook {Id}", audiobookId);
                return new AutomaticSearchBookResult
                {
                    AudiobookId = audiobookId,
                    Title = audiobook.Title ?? string.Empty,
                    Success = false,
                    Message = ex.Message
                };
            }
        }
    }
}
