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
            // First non-empty series ASIN seen on a membership — lets a consumer
            // pin the catalog identity when it starts monitoring the series.
            public string? SeriesAsin { get; set; }
            // Author frequency among OWNED books, for a "who wrote this" hint.
            public Dictionary<string, int> OwnedAuthorCounts { get; } = new(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>The user's most-built recording edition of a series.</summary>
        public sealed record SeriesHealthBestRun(
            string Label,
            string Kind,
            IReadOnlyList<string> Narrators,
            int Owned,
            int Total,
            double Completion);

        /// <summary>
        /// One series as the health computation sees it. Typed so other
        /// workflows (series triage) can reuse the exact same numbers the
        /// dashboard shows instead of re-deriving them.
        /// </summary>
        public sealed record SeriesHealthRow(
            string Name,
            string? SeriesAsin,
            IReadOnlyList<string> Authors,
            int Owned,
            int MissingTracked,
            int? CatalogTotal,
            int? Editions,
            bool Monitored,
            bool Complete,
            double? Completion,
            SeriesHealthBestRun? BestRun);

        public sealed record SeriesHealthRows(string Region, IReadOnlyList<SeriesHealthRow> Rows);

        public async Task<IActionResult> HealthAsync(CancellationToken ct)
        {
            var computed = await ComputeRowsAsync(ct);

            // JSON shape the dashboard depends on — keep it stable; `seriesAsin`
            // and `authors` are additive.
            var rows = computed.Rows
                .Select(r => new
                {
                    name = r.Name,
                    seriesAsin = r.SeriesAsin,
                    authors = r.Authors,
                    owned = r.Owned,
                    missingTracked = r.MissingTracked,
                    catalogTotal = r.CatalogTotal,
                    editions = r.Editions,
                    monitored = r.Monitored,
                    complete = r.Complete,
                    completion = r.Completion,
                    bestRun = r.BestRun == null ? null : new
                    {
                        label = r.BestRun.Label,
                        kind = r.BestRun.Kind,
                        narrators = r.BestRun.Narrators,
                        owned = r.BestRun.Owned,
                        total = r.BestRun.Total,
                        completion = r.BestRun.Completion
                    }
                })
                .ToList();

            return new OkObjectResult(new { region = computed.Region, rows });
        }

        /// <summary>
        /// Full-library aggregate; memoized ~5 min (see DashboardAggregateCache).
        /// Monitoring changes therefore show up in health/triage rows after the
        /// cache TTL; per-series decisions are merged on top by their consumers.
        /// </summary>
        public Task<SeriesHealthRows> ComputeRowsAsync(CancellationToken ct)
        {
            return _aggregateCache.GetOrCreateAsync("series-health-rows", () => ComputeRowsUncachedAsync(ct));
        }

        private async Task<SeriesHealthRows> ComputeRowsUncachedAsync(CancellationToken ct)
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
                var nameToAsin = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                if (memberships.TryGetValue(book.Id, out var ms) && ms.Count > 0)
                {
                    foreach (var m in ms)
                    {
                        var n = m.SeriesName?.Trim();
                        if (!string.IsNullOrWhiteSpace(n))
                        {
                            nameToPosition.TryAdd(n!, m.SeriesNumber);
                            nameToAsin.TryAdd(n!, string.IsNullOrWhiteSpace(m.SeriesAsin) ? null : m.SeriesAsin.Trim());
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

                    if (acc.SeriesAsin == null && nameToAsin.TryGetValue(name, out var asin) && asin != null)
                    {
                        acc.SeriesAsin = asin;
                    }

                    var workKey = SeriesWorkKey.Build(book.Title, book.Authors, position);
                    if (workKey.Length == 0)
                    {
                        continue;
                    }

                    if (owned)
                    {
                        acc.OwnedWorks.Add(workKey);
                        foreach (var author in book.Authors ?? new List<string>())
                        {
                            var a = author?.Trim();
                            if (string.IsNullOrWhiteSpace(a))
                            {
                                continue;
                            }

                            acc.OwnedAuthorCounts[a!] = acc.OwnedAuthorCounts.TryGetValue(a!, out var n) ? n + 1 : 1;
                        }
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

            // Best-edition completion per series — the "one more grab
            // finishes it" number. Computed from the cached catalog entries
            // (no network) inside this memoized refresh; a series whose best
            // narrator run is 8/9 must rank above 8 books scattered across
            // three incompatible runs.
            var bestRuns = new Dictionary<string, Application.Audiobooks.Series.SeriesRunCompletion.BestRunSummary?>(StringComparer.OrdinalIgnoreCase);
            foreach (var acc in bySeries.Values)
            {
                var summary = catalogSummaries.TryGetValue(acc.Name, out var found) ? found : null;
                if (summary?.Works is > 0)
                {
                    var entries = await _repo.GetSeriesCatalogEntriesAsync(acc.Name, region, ct);
                    var run = Application.Audiobooks.Series.SeriesRunCompletion.BestRun(entries, acc.OwnedWorks);
                    // When every run covers a single work (each book has its
                    // own narrator), "best edition" is meaningless — a 1/1
                    // badge on a four-book series reads as 100% complete.
                    // Drop it so consumers fall back to overall completion.
                    if (run != null && run.TotalWorks == 1 && summary.Works > 1)
                    {
                        run = null;
                    }
                    bestRuns[acc.Name] = run;
                }
            }

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
                    var bestRun = bestRuns.TryGetValue(acc.Name, out var br) ? br : null;
                    var authors = acc.OwnedAuthorCounts
                        .OrderByDescending(kv => kv.Value)
                        .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                        .Take(3)
                        .Select(kv => kv.Key)
                        .ToList();
                    return new SeriesHealthRow(
                        acc.Name,
                        acc.SeriesAsin,
                        authors,
                        ownedWorks,
                        missingTracked,
                        catalogTotal,
                        summary?.Editions,
                        monitoredNames.Contains(acc.Name),
                        complete,
                        catalogTotal is > 0
                            ? Math.Round(Math.Min(1.0, (double)ownedWorks / catalogTotal.Value), 3)
                            : null,
                        bestRun == null ? null : new SeriesHealthBestRun(
                            bestRun.Label,
                            bestRun.Kind,
                            bestRun.Narrators,
                            bestRun.OwnedWorks,
                            bestRun.TotalWorks,
                            Math.Round(bestRun.Completion, 3)));
                })
                .OrderByDescending(r => (r.CatalogTotal ?? (r.Owned + r.MissingTracked)) - r.Owned)
                .ThenByDescending(r => r.CatalogTotal ?? (r.Owned + r.MissingTracked))
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            _logger.LogDebug(
                "Series health: {Series} series, {WithCatalog} with cached catalog totals",
                rows.Count, rows.Count(r => r.CatalogTotal.HasValue));

            return new SeriesHealthRows(region, rows);
        }
    }
}
