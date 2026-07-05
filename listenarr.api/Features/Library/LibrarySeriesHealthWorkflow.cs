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
        private readonly ILogger<LibrarySeriesHealthWorkflow> _logger;

        public LibrarySeriesHealthWorkflow(
            IAudiobookRepository repo,
            IAudiobookFileRepository fileRepository,
            IMonitoredSeriesRepository monitoredSeriesRepository,
            IApplicationSettingsRepository settingsRepository,
            ILogger<LibrarySeriesHealthWorkflow> logger)
        {
            _repo = repo;
            _fileRepository = fileRepository;
            _monitoredSeriesRepository = monitoredSeriesRepository;
            _settingsRepository = settingsRepository;
            _logger = logger;
        }

        internal sealed record SeriesAccumulator(string Name)
        {
            public int Owned { get; set; }
            public int MissingTracked { get; set; }
        }

        public async Task<IActionResult> HealthAsync(CancellationToken ct)
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
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (memberships.TryGetValue(book.Id, out var ms) && ms.Count > 0)
                {
                    foreach (var m in ms)
                    {
                        var n = m.SeriesName?.Trim();
                        if (!string.IsNullOrWhiteSpace(n))
                        {
                            names.Add(n!);
                        }
                    }
                }
                else if (!string.IsNullOrWhiteSpace(book.Series))
                {
                    names.Add(book.Series!.Trim());
                }

                if (names.Count == 0)
                {
                    continue;
                }

                var owned = fileCounts.TryGetValue(book.Id, out var c) && c > 0;
                foreach (var name in names)
                {
                    if (!bySeries.TryGetValue(name, out var acc))
                    {
                        acc = new SeriesAccumulator(name);
                        bySeries[name] = acc;
                    }

                    if (owned)
                    {
                        acc.Owned++;
                    }
                    else
                    {
                        acc.MissingTracked++;
                    }
                }
            }

            var allNames = bySeries.Keys.ToList();
            var catalogTotals = await _repo.GetSeriesCatalogTotalsAsync(allNames, region, ct);

            var monitored = await _monitoredSeriesRepository.GetAllAsync(ct);
            var monitoredNames = new HashSet<string>(
                monitored.Select(m => m.SeriesName.Trim()),
                StringComparer.OrdinalIgnoreCase);

            var rows = bySeries.Values
                .Select(acc =>
                {
                    int? catalogTotal = catalogTotals.TryGetValue(acc.Name, out var total) ? total : null;
                    var complete = catalogTotal.HasValue
                        ? acc.Owned >= catalogTotal.Value
                        : acc.MissingTracked == 0;
                    return new
                    {
                        name = acc.Name,
                        owned = acc.Owned,
                        missingTracked = acc.MissingTracked,
                        catalogTotal,
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

            return new OkObjectResult(new { region, rows });
        }
    }
}
