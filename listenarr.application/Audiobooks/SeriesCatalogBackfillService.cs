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
using Listenarr.Domain.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks
{
    /// <summary>
    /// Caches the Audible catalog for series that merely appear in the library,
    /// not just ones the user has explicitly monitored. Without this, the
    /// dashboard's series completeness is "unknown" for everything unmonitored.
    /// Caching is not monitoring — this only writes SeriesCacheEntry rows and
    /// never triggers searches.
    ///
    /// Conservative by design: only series with at least
    /// <see cref="MinBooksForRealSeries"/> books in the library are considered
    /// (Audible labels standalone books as 1-member "series"), already-cached
    /// series are skipped, and only a small batch of Audible fetches runs per
    /// cycle so the API isn't hammered. Opt out with
    /// LISTENARR_AUTO_CACHE_SERIES_CATALOGS=false.
    /// </summary>
    public class SeriesCatalogBackfillService : BackgroundService
    {
        // Audible labels standalone books as 1-member series; require 2+ owned
        // books before treating a series name as worth a catalog fetch.
        internal const int MinBooksForRealSeries = 2;

        private const int MaxFetchesPerCycle = 20;
        private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan CycleInterval = TimeSpan.FromHours(6);
        private static readonly TimeSpan DelayBetweenFetches = TimeSpan.FromSeconds(2);

        private readonly ILogger<SeriesCatalogBackfillService> _logger;
        private readonly IServiceScopeFactory _serviceScopeFactory;

        public SeriesCatalogBackfillService(
            ILogger<SeriesCatalogBackfillService> logger,
            IServiceScopeFactory serviceScopeFactory)
        {
            _logger = logger;
            _serviceScopeFactory = serviceScopeFactory;
        }

        private static bool IsEnabled()
        {
            var raw = Environment.GetEnvironmentVariable("LISTENARR_AUTO_CACHE_SERIES_CATALOGS");
            // Default on; only an explicit false/0 disables it.
            return !string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)
                && raw != "0";
        }

        /// <summary>
        /// Distinct series names worth caching: those with at least
        /// <paramref name="minBooks"/> books tracked in the library. Returns one
        /// representative original-cased name per series.
        /// </summary>
        internal static List<string> SelectSeriesToBackfill(IEnumerable<Audiobook> books, int minBooks)
        {
            return books
                .Select(b => b.Series)
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!.Trim())
                .GroupBy(s => s, StringComparer.OrdinalIgnoreCase)
                .Where(g => g.Count() >= minBooks)
                .Select(g => g.First())
                .ToList();
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "SeriesCatalogBackfillService started. In-library series catalogs will be cached every {Hours}h",
                CycleInterval.TotalHours);

            try
            {
                await Task.Delay(StartupDelay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (IsEnabled())
                    {
                        await RunCycleAsync(stoppingToken);
                    }
                    else
                    {
                        _logger.LogDebug(
                            "SeriesCatalogBackfillService disabled via LISTENARR_AUTO_CACHE_SERIES_CATALOGS; skipping cycle");
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogError(ex, "Error during series catalog backfill cycle");
                }

                try
                {
                    await Task.Delay(CycleInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            _logger.LogInformation("SeriesCatalogBackfillService stopped");
        }

        private async Task RunCycleAsync(CancellationToken stoppingToken)
        {
            using var scope = _serviceScopeFactory.CreateScope();
            var audiobookRepository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            var catalogService = scope.ServiceProvider.GetRequiredService<ISeriesCatalogService>();
            var settingsRepository = scope.ServiceProvider.GetRequiredService<IApplicationSettingsRepository>();

            var settings = await settingsRepository.GetAsync(stoppingToken);
            var region = string.IsNullOrWhiteSpace(settings?.DefaultSearchRegion)
                ? "us"
                : settings!.DefaultSearchRegion;

            var books = await audiobookRepository.GetAllAsync();
            var seriesNames = SelectSeriesToBackfill(books, MinBooksForRealSeries);

            // Random order so that, when more series need caching than the
            // per-cycle cap allows, coverage rotates instead of starving the tail.
            var shuffled = seriesNames.OrderBy(_ => Guid.NewGuid());

            var fetched = 0;
            var skipped = 0;
            foreach (var name in shuffled)
            {
                stoppingToken.ThrowIfCancellationRequested();

                if (fetched >= MaxFetchesPerCycle)
                {
                    break;
                }

                if (await catalogService.HasCachedCatalogAsync(name, region, stoppingToken))
                {
                    skipped++;
                    continue;
                }

                try
                {
                    await catalogService.GetCatalogAsync(name, region, cancellationToken: stoppingToken);
                    fetched++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to cache series catalog for {Series}", name);
                }

                try
                {
                    await Task.Delay(DelayBetweenFetches, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            _logger.LogInformation(
                "SeriesCatalogBackfill cycle complete: {Total} library series, {Skipped} already cached, {Fetched} catalogs fetched",
                seriesNames.Count, skipped, fetched);
        }
    }
}
