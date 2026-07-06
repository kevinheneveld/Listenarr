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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.HostedServices.Catalog
{
    /// <summary>
    /// Caches the Audible catalog for series that merely appear in the library,
    /// not just ones the user has explicitly monitored. Without this, series
    /// completeness is "unknown" for everything unmonitored. Caching is not
    /// monitoring — this only writes SeriesCacheEntry rows and never triggers
    /// searches.
    /// </summary>
    public class SeriesCatalogBackfillService(
        ILogger<SeriesCatalogBackfillService> logger,
        ISeriesCatalogBackfillProcessor processor,
        IWorkerCycleRunner cycleRunner) : BackgroundService
    {
        private static readonly TimeSpan StartupDelay = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan CycleInterval = TimeSpan.FromHours(6);

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            logger.LogInformation(
                "SeriesCatalogBackfillService started. In-library series catalogs will be cached every {Hours}h",
                CycleInterval.TotalHours);

            await cycleRunner.RunPeriodicAsync(
                nameof(SeriesCatalogBackfillService),
                initialDelay: StartupDelay,
                intervalProvider: () => CycleInterval,
                runCycle: processor.RunCycleAsync,
                stoppingToken);

            logger.LogInformation("SeriesCatalogBackfillService stopped");
        }
    }

    public sealed record SeriesBackfillRunResult(int TotalSeries, int AlreadyCached, int Fetched, int Failed)
    {
        public int Remaining => Math.Max(0, TotalSeries - AlreadyCached - Fetched);
    }

    public interface ISeriesCatalogBackfillProcessor
    {
        Task RunCycleAsync(CancellationToken cancellationToken);

        /// <summary>
        /// One immediate backfill pass with an explicit fetch cap — the
        /// dashboard's "Backfill now" trigger. Ignores the periodic service's
        /// env-disable: an explicit user action outranks the automation toggle.
        /// </summary>
        Task<SeriesBackfillRunResult> RunOnceAsync(int maxFetches, CancellationToken cancellationToken);
    }

    public class SeriesCatalogBackfillProcessor : ISeriesCatalogBackfillProcessor
    {
        // Audible labels standalone books as 1-member series; require 2+ tracked
        // books before treating a series name as worth a catalog fetch.
        internal const int MinBooksForRealSeries = 2;

        private const int MaxFetchesPerCycle = 20;
        private static readonly TimeSpan DelayBetweenFetches = TimeSpan.FromSeconds(2);

        private readonly ILogger<SeriesCatalogBackfillProcessor> _logger;
        private readonly IServiceScopeFactory _serviceScopeFactory;

        public SeriesCatalogBackfillProcessor(
            ILogger<SeriesCatalogBackfillProcessor> logger,
            IServiceScopeFactory serviceScopeFactory)
        {
            _logger = logger;
            _serviceScopeFactory = serviceScopeFactory;
        }

        internal static bool IsEnabled()
        {
            var raw = Environment.GetEnvironmentVariable("LISTENARR_AUTO_CACHE_SERIES_CATALOGS");
            // Default on; only an explicit false/0 disables it.
            return !string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)
                && raw != "0";
        }

        /// <summary>
        /// Distinct series names worth caching: those with at least
        /// <paramref name="minBooks"/> tracked books. Each inner collection is
        /// one book's series names (memberships plus the legacy field); a book
        /// counts once per series even when both spellings appear on it.
        /// Returns one representative original-cased name per series.
        /// </summary>
        internal static List<string> SelectSeriesToBackfill(
            IEnumerable<IReadOnlyCollection<string>> perBookSeriesNames,
            int minBooks)
        {
            var counts = new Dictionary<string, (string Display, int Count)>(StringComparer.OrdinalIgnoreCase);
            foreach (var names in perBookSeriesNames)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var raw in names)
                {
                    var name = raw?.Trim();
                    if (string.IsNullOrWhiteSpace(name) || !seen.Add(name))
                    {
                        continue;
                    }

                    counts[name] = counts.TryGetValue(name, out var existing)
                        ? (existing.Display, existing.Count + 1)
                        : (name, 1);
                }
            }

            return counts.Values
                .Where(v => v.Count >= minBooks)
                .Select(v => v.Display)
                .ToList();
        }

        public async Task RunCycleAsync(CancellationToken stoppingToken)
        {
            if (!IsEnabled())
            {
                _logger.LogDebug(
                    "SeriesCatalogBackfillService disabled via LISTENARR_AUTO_CACHE_SERIES_CATALOGS; skipping cycle");
                return;
            }

            await RunOnceAsync(MaxFetchesPerCycle, stoppingToken);
        }

        public async Task<SeriesBackfillRunResult> RunOnceAsync(int maxFetches, CancellationToken stoppingToken)
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
            var memberships = await audiobookRepository.GetAllSeriesMembershipsGroupedByAudiobookIdAsync(stoppingToken);

            var perBook = books.Select(b =>
            {
                var names = new List<string>();
                if (memberships.TryGetValue(b.Id, out var ms))
                {
                    names.AddRange(ms.Select(m => m.SeriesName).Where(n => !string.IsNullOrWhiteSpace(n))!);
                }
                if (!string.IsNullOrWhiteSpace(b.Series))
                {
                    names.Add(b.Series!);
                }
                return (IReadOnlyCollection<string>)names;
            });

            var seriesNames = SelectSeriesToBackfill(perBook, MinBooksForRealSeries);

            // Random order so that, when more series need caching than the
            // per-cycle cap allows, coverage rotates instead of starving the tail.
            var shuffled = seriesNames.OrderBy(_ => Guid.NewGuid());

            var fetched = 0;
            var skipped = 0;
            var failed = 0;
            foreach (var name in shuffled)
            {
                stoppingToken.ThrowIfCancellationRequested();

                if (fetched >= maxFetches)
                {
                    break;
                }

                var cached = await audiobookRepository.GetCachedSeriesByNameAsync(name, region);
                if (cached?.CatalogBooks is { Count: > 0 })
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
                    failed++;
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

            return new SeriesBackfillRunResult(seriesNames.Count, skipped, fetched, failed);
        }
    }
}
