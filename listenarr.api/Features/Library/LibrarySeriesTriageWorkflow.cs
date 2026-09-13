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

using Listenarr.Application.Audiobooks.Monitoring;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    /// <summary>
    /// "Series you're not collecting": every series the user OWNS books from
    /// (file-backed) that is not monitored, so they can decide per series to
    /// collect the rest (monitor it — existing endpoint) or dismiss it. The
    /// rows are the dashboard's own series-health numbers (same cache, same
    /// work-key counting); decisions are read fresh from the repository and
    /// merged on top, so a dismissal shows immediately while a new monitor
    /// shows after the ~5 min aggregate cache TTL.
    ///
    /// Live at introduction (2026-09-12): 493 series with tracked books, 52
    /// monitored, 278 unmonitored with owned books — 905 owned books, 139 of
    /// those series holding a single owned book.
    /// </summary>
    public sealed class LibrarySeriesTriageWorkflow
    {
        private readonly LibrarySeriesHealthWorkflow _health;
        private readonly ISeriesTriageDecisionRepository _decisions;
        private readonly ILogger<LibrarySeriesTriageWorkflow> _logger;

        public LibrarySeriesTriageWorkflow(
            LibrarySeriesHealthWorkflow health,
            ISeriesTriageDecisionRepository decisions,
            ILogger<LibrarySeriesTriageWorkflow> logger)
        {
            _health = health;
            _decisions = decisions;
            _logger = logger;
        }

        public sealed class SeriesTriageDecisionRequest
        {
            public string SeriesName { get; set; } = string.Empty;
            public string? SeriesAsin { get; set; }
            public string? Note { get; set; }
        }

        public sealed record SeriesTriageSummary(
            int Candidates,
            int Dismissed,
            int OwnedBooksInCandidates,
            int SingleBook,
            int WithCatalog);

        public sealed record SeriesTriageRow(
            string Name,
            string? SeriesAsin,
            IReadOnlyList<string> Authors,
            int Owned,
            int MissingTracked,
            int? CatalogTotal,
            double? Completion,
            int? Editions,
            bool Dismissed,
            DateTime? DismissedAt,
            string? Note);

        public sealed record SeriesTriageResponse(SeriesTriageSummary Summary, IReadOnlyList<SeriesTriageRow> Rows);

        public async Task<SeriesTriageResponse> GetAsync(bool includeDismissed, CancellationToken ct)
        {
            var computed = await _health.ComputeRowsAsync(ct);
            var decisions = (await _decisions.GetAllAsync(ct))
                .GroupBy(d => d.SeriesNameNormalized, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            var all = new List<SeriesTriageRow>();
            foreach (var row in computed.Rows)
            {
                if (row.Monitored || row.Owned <= 0)
                {
                    continue;
                }

                decisions.TryGetValue(SeriesNameNormalizer.Normalize(row.Name), out var decision);
                var dismissed = decision != null && string.Equals(decision.Decision, Domain.Audiobooks.SeriesTriageDecision.Dismissed, StringComparison.OrdinalIgnoreCase);
                all.Add(new SeriesTriageRow(
                    row.Name,
                    row.SeriesAsin ?? decision?.SeriesAsin,
                    row.Authors,
                    row.Owned,
                    row.MissingTracked,
                    row.CatalogTotal,
                    row.Completion,
                    row.Editions,
                    dismissed,
                    dismissed ? decision!.CreatedAt : null,
                    dismissed ? decision!.Note : null));
            }

            var candidates = all.Where(r => !r.Dismissed).ToList();
            var summary = new SeriesTriageSummary(
                candidates.Count,
                all.Count(r => r.Dismissed),
                candidates.Sum(r => r.Owned),
                candidates.Count(r => r.Owned == 1),
                candidates.Count(r => r.CatalogTotal.HasValue));

            var rows = (includeDismissed ? all : candidates)
                .OrderByDescending(r => r.Owned)
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new SeriesTriageResponse(summary, rows);
        }

        public async Task<IActionResult> DismissAsync(SeriesTriageDecisionRequest? request, CancellationToken ct)
        {
            var name = request?.SeriesName?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                return new BadRequestObjectResult(new { message = "seriesName is required." });
            }

            var stored = await _decisions.UpsertAsync(new Domain.Audiobooks.SeriesTriageDecision
            {
                SeriesName = name!,
                SeriesNameNormalized = SeriesNameNormalizer.Normalize(name),
                SeriesAsin = string.IsNullOrWhiteSpace(request!.SeriesAsin) ? null : request.SeriesAsin!.Trim(),
                Decision = Domain.Audiobooks.SeriesTriageDecision.Dismissed,
                Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note!.Trim(),
                CreatedAt = DateTime.UtcNow
            }, ct);

            _logger.LogInformation("Series triage: dismissed '{Series}'", name);
            return new OkObjectResult(new
            {
                seriesName = stored.SeriesName,
                seriesAsin = stored.SeriesAsin,
                decision = stored.Decision,
                note = stored.Note,
                createdAt = stored.CreatedAt
            });
        }

        public async Task<IActionResult> UndismissAsync(SeriesTriageDecisionRequest? request, CancellationToken ct)
        {
            var name = request?.SeriesName?.Trim();
            if (string.IsNullOrWhiteSpace(name))
            {
                return new BadRequestObjectResult(new { message = "seriesName is required." });
            }

            var removed = await _decisions.DeleteByNormalizedNameAsync(SeriesNameNormalizer.Normalize(name), ct);
            if (removed)
            {
                _logger.LogInformation("Series triage: decision cleared for '{Series}'", name);
            }

            return new OkObjectResult(new { seriesName = name, removed });
        }
    }
}
