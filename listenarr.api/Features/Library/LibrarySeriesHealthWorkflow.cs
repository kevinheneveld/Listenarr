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

using Listenarr.Application.Audiobooks.Series;
using Listenarr.Application.Configuration.Contracts.Repositories;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    /// <summary>
    /// Catalog-aware series health for the dashboard: tracked books grouped by
    /// series, joined with cached Audible catalog totals so "complete" can mean
    /// "you own every book the catalog says exists", not merely "no gaps among
    /// the records you happen to track". Series without a cached catalog fall
    /// back to tracked-only completeness and are flagged as such.
    /// </summary>
    public sealed class LibrarySeriesHealthWorkflow
    {
        private readonly IAudiobookRepository _repo;
        private readonly IAudiobookFileRepository _fileRepository;
        private readonly IMonitoredSeriesRepository _monitoredSeriesRepository;
        private readonly IApplicationSettingsRepository _settingsRepository;
        private readonly DashboardAggregateCache _aggregateCache;
        private readonly ILogger<LibrarySeriesHealthWorkflow> _logger;

        public LibrarySeriesHealthWorkflow(
            IAudiobookRepository repo,
            IAudiobookFileRepository fileRepository,
            IMonitoredSeriesRepository monitoredSeriesRepository,
            IApplicationSettingsRepository settingsRepository,
            DashboardAggregateCache aggregateCache,
            ILogger<LibrarySeriesHealthWorkflow> logger)
        {
            _repo = repo;
            _fileRepository = fileRepository;
            _monitoredSeriesRepository = monitoredSeriesRepository;
            _settingsRepository = settingsRepository;
            _aggregateCache = aggregateCache;
            _logger = logger;
        }

        internal sealed record SeriesAccumulator(string Name)
        {
            // WORK keys, not record counts: two recordings of the same book
            // (or a re-added duplicate) are one logical book to the user.
            public HashSet<string> OwnedWorks { get; } = new(StringComparer.Ordinal);
            public HashSet<string> TrackedNoFileWorks { get; } = new(StringComparer.Ordinal);
        }

        public async Task<IActionResult> HealthAsync(CancellationToken ct)
        {
            // Full-library aggregate; memoized ~5 min (see DashboardAggregateCache).
            var payload = await _aggregateCache.GetOrCreateAsync<object>("series-health", () => ComputeHealthPayloadAsync(ct));
            return new OkObjectResult(payload);
        }

        private async Task<object> ComputeHealthPayloadAsync(CancellationToken ct)
        {
            var settings = await _settingsRepository.GetAsync(ct);
            var region = string.IsNullOrWhiteSpace(settings?.DefaultSearchRegion)
                ? "us"
                : settings!.DefaultSearchRegion;

            var books = await _repo.GetAllAsync();
            var memberships = await _repo.GetAllSeriesMembershipsGroupedByAudiobookIdAsync(ct);
            var fileCounts = await _fileRepository.GetCountsByAudiobookIdAsync(ct);

            // Group tracked books by series name — memberships first, legacy
            // `Series` field as fallback, mirroring the client-side aggregation
            // the dashboard used before this endpoint existed.
            var bySeries = new Dictionary<string, SeriesAccumulator>(StringComparer.OrdinalIgnoreCase);
            foreach (var book in books)
            {
                // Per-series position matters for the work key (volume-numbered
                // sets must stay distinct), so track (name → position) pairs.
                var nameToPosition = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                if (memberships.TryGetValue(book.Id, out var ms) && ms.Count > 0)
                {
                    foreach (var m in ms)
                    {
                        var n = m.SeriesName?.Trim();
                        if (!string.IsNullOrWhiteSpace(n))
                        {
                            nameToPosition.TryAdd(n!, m.SeriesNumber);
                        }
                    }
                }
                else if (!string.IsNullOrWhiteSpace(book.Series))
                {
                    nameToPosition.TryAdd(book.Series!.Trim(), book.SeriesNumber);
                }

                if (nameToPosition.Count == 0)
                {
                    continue;
                }

                var owned = fileCounts.TryGetValue(book.Id, out var c) && c > 0;
                foreach (var (name, position) in nameToPosition)
                {
                    if (!bySeries.TryGetValue(name, out var acc))
                    {
                        acc = new SeriesAccumulator(name);
                        bySeries[name] = acc;
                    }

                    var workKey = SeriesWorkKey.Build(book.Title, book.Authors, position);
                    if (workKey.Length == 0)
                    {
                        continue;
                    }

                    if (owned)
                    {
                        acc.OwnedWorks.Add(workKey);
                    }
                    else
                    {
                        acc.TrackedNoFileWorks.Add(workKey);
                    }
                }
            }

            var allNames = bySeries.Keys.ToList();
            var catalogSummaries = await _repo.GetSeriesCatalogSummariesAsync(allNames, region, ct);

            var monitored = await _monitoredSeriesRepository.GetAllAsync(ct);
            var monitoredNames = new HashSet<string>(
                monitored.Select(m => m.SeriesName.Trim()),
                StringComparer.OrdinalIgnoreCase);

            var rows = bySeries.Values
                .Select(acc =>
                {
                    var summary = catalogSummaries.TryGetValue(acc.Name, out var found) ? found : null;
                    int? catalogTotal = summary?.Works;
                    var ownedWorks = acc.OwnedWorks.Count;
                    // A work is only "missing" while no recording of it is owned.
                    var missingTracked = acc.TrackedNoFileWorks.Count(k => !acc.OwnedWorks.Contains(k));
                    var complete = catalogTotal.HasValue
                        ? ownedWorks >= catalogTotal.Value
                        : missingTracked == 0;
                    return new
                    {
                        name = acc.Name,
                        owned = ownedWorks,
                        missingTracked,
                        catalogTotal,
                        editions = summary?.Editions,
                        monitored = monitoredNames.Contains(acc.Name),
                        complete
                    };
                })
                .OrderByDescending(r => (r.catalogTotal ?? (r.owned + r.missingTracked)) - r.owned)
                .ThenByDescending(r => r.catalogTotal ?? (r.owned + r.missingTracked))
                .ThenBy(r => r.name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _logger.LogDebug(
                "Series health: {Series} series, {WithCatalog} with cached catalog totals",
                rows.Count, rows.Count(r => r.catalogTotal.HasValue));

            return new { region, rows };
        }
    }
}
