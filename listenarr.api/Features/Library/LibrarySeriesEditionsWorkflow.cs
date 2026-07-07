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
    /// Recording-edition ("run") view of one series: the cached catalog's
    /// recordings grouped into production runs (a narration run vs. a
    /// full-cast dramatization of the same works), each with works-based
    /// coverage and add-ready metadata for the run's missing works — so
    /// gap-filling can stay within one production instead of mixing an
    /// R.C. Bray book 1 with a theater production of book 2. Heuristic
    /// grouping; see <see cref="RecordingEditionClassifier"/>.
    /// </summary>
    public sealed class LibrarySeriesEditionsWorkflow
    {
        private readonly IAudiobookRepository _repo;
        private readonly IAudiobookFileRepository _fileRepository;
        private readonly IApplicationSettingsRepository _settingsRepository;

        public LibrarySeriesEditionsWorkflow(
            IAudiobookRepository repo,
            IAudiobookFileRepository fileRepository,
            IApplicationSettingsRepository settingsRepository)
        {
            _repo = repo;
            _fileRepository = fileRepository;
            _settingsRepository = settingsRepository;
        }

        public async Task<IActionResult> EditionsAsync(string name, string? region, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return new BadRequestObjectResult(new { message = "Series name is required" });
            }

            if (string.IsNullOrWhiteSpace(region))
            {
                var settings = await _settingsRepository.GetAsync(ct);
                region = string.IsNullOrWhiteSpace(settings?.DefaultSearchRegion)
                    ? "us"
                    : settings!.DefaultSearchRegion;
            }

            var entries = await _repo.GetSeriesCatalogEntriesAsync(name, region!, ct);
            var ownedWorkKeys = await CollectOwnedWorkKeysAsync(name, ct);

            if (entries.Count == 0)
            {
                return new OkObjectResult(new
                {
                    name,
                    region,
                    totalWorks = 0,
                    ownedWorks = ownedWorkKeys.Count,
                    catalogCached = false,
                    runs = Array.Empty<object>()
                });
            }

            var allWorkKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                var key = SeriesWorkKey.Build(entry.Title, entry.Authors, entry.SeriesNumber);
                if (key.Length > 0)
                {
                    allWorkKeys.Add(key);
                }
            }

            var runs = RecordingEditionClassifier.GroupIntoRuns(
                entries,
                e => RecordingEditionClassifier.Classify(e.Title, e.Subtitle, e.Narrators, e.Publisher, e.PublishedDate));

            var runRows = runs.Select(run =>
            {
                // Works within this run, keyed; first entry per work is the
                // run's candidate recording for that work.
                var byWork = new Dictionary<string, Domain.Audiobooks.CachedSeriesCatalogBook>(StringComparer.Ordinal);
                foreach (var (entry, _) in run.Entries)
                {
                    var key = SeriesWorkKey.Build(entry.Title, entry.Authors, entry.SeriesNumber);
                    if (key.Length > 0)
                    {
                        byWork.TryAdd(key, entry);
                    }
                }

                var ownedInRun = byWork.Keys.Count(ownedWorkKeys.Contains);
                var missing = byWork
                    .Where(kv => !ownedWorkKeys.Contains(kv.Key))
                    .Select(kv => kv.Value)
                    .Where(e => !string.IsNullOrWhiteSpace(e.Asin))
                    .Select(e => new
                    {
                        asin = e.Asin,
                        title = e.Title,
                        subtitle = e.Subtitle,
                        authors = e.Authors,
                        narrators = e.Narrators,
                        publisher = e.Publisher,
                        language = e.Language,
                        runtime = e.Runtime,
                        imageUrl = e.ImageUrl,
                        series = e.Series ?? name,
                        seriesNumber = e.SeriesNumber,
                        publishedDate = e.PublishedDate,
                        isbn = e.Isbn
                    })
                    .ToList();

                return new
                {
                    label = run.Label,
                    kind = run.Kind.ToString(),
                    narrators = run.Narrators,
                    publisher = run.Publisher,
                    totalWorks = byWork.Count,
                    ownedWorks = ownedInRun,
                    recordings = run.Entries.Count,
                    missingEntries = missing
                };
            }).ToList();

            return new OkObjectResult(new
            {
                name,
                region,
                totalWorks = allWorkKeys.Count,
                ownedWorks = allWorkKeys.Count(ownedWorkKeys.Contains),
                catalogCached = true,
                runs = runRows
            });
        }

        /// <summary>
        /// Work keys of the user's tracked-with-files books in this series —
        /// same keying the health endpoint uses, so the two can't disagree.
        /// </summary>
        private async Task<HashSet<string>> CollectOwnedWorkKeysAsync(string seriesName, CancellationToken ct)
        {
            var books = await _repo.GetAllAsync();
            var memberships = await _repo.GetAllSeriesMembershipsGroupedByAudiobookIdAsync(ct);
            var fileCounts = await _fileRepository.GetCountsByAudiobookIdAsync(ct);

            var owned = new HashSet<string>(StringComparer.Ordinal);
            foreach (var book in books)
            {
                if (!(fileCounts.TryGetValue(book.Id, out var c) && c > 0))
                {
                    continue;
                }

                string? position = null;
                var inSeries = false;
                if (memberships.TryGetValue(book.Id, out var ms) && ms.Count > 0)
                {
                    var match = ms.FirstOrDefault(m =>
                        string.Equals(m.SeriesName?.Trim(), seriesName, StringComparison.OrdinalIgnoreCase));
                    if (match != null)
                    {
                        inSeries = true;
                        position = match.SeriesNumber;
                    }
                }
                else if (string.Equals(book.Series?.Trim(), seriesName, StringComparison.OrdinalIgnoreCase))
                {
                    inSeries = true;
                    position = book.SeriesNumber;
                }

                if (!inSeries)
                {
                    continue;
                }

                var key = SeriesWorkKey.Build(book.Title, book.Authors, position);
                if (key.Length > 0)
                {
                    owned.Add(key);
                }
            }

            return owned;
        }
    }
}
