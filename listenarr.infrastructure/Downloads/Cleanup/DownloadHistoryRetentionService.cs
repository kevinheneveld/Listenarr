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

using Listenarr.Application.Downloads.Contracts.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Downloads.Cleanup
{
    /// <summary>
    /// Background service that periodically purges old terminal rows from the <c>Downloads</c>
    /// table (the download/import history). Unlike the processing-job table, download history had
    /// no retention at all, so it grew unbounded on long-running instances. Only terminal records
    /// (Moved / Completed / Failed / ImportBlocked) older than the retention window are removed;
    /// active downloads are never touched. Deleting a history row does not affect the library —
    /// audiobook files live in their own tables/folders.
    /// Runs shortly after startup (to drain any accumulated backlog) and then daily.
    /// </summary>
    public class DownloadHistoryRetentionService : BackgroundService
    {
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<DownloadHistoryRetentionService> _logger;

        // Terminal download-history rows older than this are eligible for deletion. Kept as a
        // constant (mirroring DownloadProcessingJobCleanupService) to avoid a settings migration.
        internal const int RetentionDays = 30;

        private readonly TimeSpan _initialDelay = TimeSpan.FromMinutes(5);
        private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(24);

        public DownloadHistoryRetentionService(
            IServiceScopeFactory scopeFactory,
            ILogger<DownloadHistoryRetentionService> logger)
        {
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "Download history retention service starting (retention {RetentionDays}d, interval {IntervalHours}h)",
                RetentionDays,
                _cleanupInterval.TotalHours);

            try
            {
                await Task.Delay(_initialDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                await RunCleanupAsync(stoppingToken);

                try
                {
                    await Task.Delay(_cleanupInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            _logger.LogInformation("Download history retention service stopping");
        }

        /// <summary>
        /// Runs a single retention pass. Internal so it can be exercised directly in tests
        /// without driving the background loop.
        /// </summary>
        internal async Task RunCleanupAsync(CancellationToken cancellationToken)
        {
            try
            {
                var cutoff = DateTime.UtcNow.AddDays(-RetentionDays);

                using var scope = _scopeFactory.CreateScope();
                var downloadRepository = scope.ServiceProvider.GetRequiredService<IDownloadRepository>();
                var removed = await downloadRepository.DeleteTerminalOlderThanAsync(cutoff, cancellationToken);

                if (removed > 0)
                {
                    _logger.LogInformation(
                        "Download history retention removed {Count} terminal record(s) older than {RetentionDays}d",
                        removed,
                        RetentionDays);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error during download history retention pass");
            }
        }
    }
}
