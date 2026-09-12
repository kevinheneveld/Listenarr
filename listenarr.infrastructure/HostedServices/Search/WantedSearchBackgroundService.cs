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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.HostedServices.Search
{
    /// <summary>
    /// Drains <see cref="IWantedSearchQueue"/> one book at a time through the sweep's
    /// per-book pipeline (<see cref="IAutomaticSearchInvoker.SearchAudiobookNowAsync"/>):
    /// active-download and cutoff checks, result filters, blocklist, LastSearchTime
    /// bump, and the sidebar activity broadcast all come for free. Books are spaced
    /// by the same <c>AutomaticSearchBookDelaySeconds</c> throttle the sweep uses —
    /// a wanted-list batch is a sweep the user asked for, not a single manual search.
    /// A failing book is counted and skipped; only host shutdown stops the loop.
    /// </summary>
    public sealed class WantedSearchBackgroundService : BackgroundService
    {
        /// <summary>Used when the settings read fails; matches the setting's default.</summary>
        public static readonly TimeSpan FallbackBookDelay = TimeSpan.FromSeconds(5);

        private readonly IWantedSearchQueue _queue;
        private readonly IAutomaticSearchInvoker _invoker;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<WantedSearchBackgroundService> _logger;
        private readonly IHubBroadcaster? _hubBroadcaster;
        private readonly TimeSpan? _bookDelayOverride;

        public WantedSearchBackgroundService(
            IWantedSearchQueue queue,
            IAutomaticSearchInvoker invoker,
            IServiceScopeFactory scopeFactory,
            ILogger<WantedSearchBackgroundService> logger,
            IHubBroadcaster? hubBroadcaster = null,
            TimeSpan? bookDelayOverride = null)
        {
            _queue = queue;
            _invoker = invoker;
            _scopeFactory = scopeFactory;
            _logger = logger;
            _hubBroadcaster = hubBroadcaster;
            _bookDelayOverride = bookDelayOverride;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("WantedSearchBackgroundService started");

            while (!stoppingToken.IsCancellationRequested)
            {
                // Cancel() swaps the channel, so re-resolve the reader after each batch.
                var reader = _queue.Reader;
                try
                {
                    await foreach (var audiobookId in reader.ReadAllAsync(stoppingToken))
                    {
                        await ProcessOneAsync(audiobookId, stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }

                // ReadAllAsync only returns when the channel was completed by Cancel();
                // loop straight back onto the replacement channel.
            }

            _logger.LogInformation("WantedSearchBackgroundService stopped");
        }

        private async Task ProcessOneAsync(int audiobookId, CancellationToken stoppingToken)
        {
            var batchToken = _queue.BatchToken;
            if (batchToken.IsCancellationRequested)
            {
                // Race guard: an id read from the old channel after Cancel() ran.
                return;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, batchToken);

            var title = await ResolveTitleAsync(audiobookId);
            _queue.MarkStarted(audiobookId, title);
            await BroadcastAsync(stoppingToken);

            try
            {
                var result = await _invoker.SearchAudiobookNowAsync(audiobookId, linked.Token);
                _queue.MarkCompleted(audiobookId, result.Success, result.DownloadsQueued);
                _logger.LogInformation(
                    "Wanted search: {Title} ({Id}) — {Outcome}",
                    string.IsNullOrWhiteSpace(result.Title) ? title ?? "audiobook" : result.Title,
                    audiobookId,
                    result.Success
                        ? (result.DownloadsQueued > 0 ? $"grabbed {result.DownloadsQueued}" : "nothing to grab")
                        : (result.Message ?? "failed"));
            }
            catch (OperationCanceledException) when (batchToken.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Wanted search: batch cancelled while searching audiobook {Id}", audiobookId);
                _queue.MarkCompleted(audiobookId, false, 0);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Wanted search: search failed for audiobook {Id}", audiobookId);
                _queue.MarkCompleted(audiobookId, false, 0);
            }

            _queue.MarkBatchFinishedIfDrained();
            await BroadcastAsync(stoppingToken);

            var delay = _bookDelayOverride ?? await ReadBookDelayAsync();
            if (delay > TimeSpan.Zero && !linked.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(delay, linked.Token);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Batch cancelled mid-delay: nothing to wait for any more.
                }
            }
        }

        private async Task<string?> ResolveTitleAsync(int audiobookId)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var audiobooks = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
                return (await audiobooks.GetByIdAsync(audiobookId))?.Title;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Wanted search: could not resolve title for audiobook {Id}", audiobookId);
                return null;
            }
        }

        private async Task<TimeSpan> ReadBookDelayAsync()
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var config = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
                var settings = await config.GetApplicationSettingsAsync();
                return TimeSpan.FromSeconds(Math.Clamp(settings.AutomaticSearchBookDelaySeconds, 0, 300));
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Wanted search: could not read AutomaticSearchBookDelaySeconds; using {Seconds}s", FallbackBookDelay.TotalSeconds);
                return FallbackBookDelay;
            }
        }

        private async Task BroadcastAsync(CancellationToken ct)
        {
            if (_hubBroadcaster == null)
            {
                return;
            }

            try
            {
                await _hubBroadcaster.BroadcastAsync("WantedSearchProgress", _queue.Snapshot(), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Wanted search: progress broadcast failed");
            }
        }
    }
}
