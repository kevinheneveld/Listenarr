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

using Listenarr.Application.Audiobooks.Verification;
using Listenarr.Application.Configuration.Contracts.Repositories;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Verification
{
    /// <summary>
    /// Read-only status for the dashboard's background-activity panel: what
    /// the verification worker is doing right now, how deep the queue is, how
    /// fast it has been running, and how far the series-catalog backfill has
    /// gotten. Days of nice-priority CPU with no visible progress is healthy
    /// behavior that LOOKS like a hang — this endpoint makes it legible.
    /// </summary>
    public sealed class VerificationQueueStatusWorkflow
    {
        private readonly ILibraryVerificationQueueService _queue;
        private readonly IVerificationJobRepository _jobRepository;
        private readonly IAudiobookRepository _audiobookRepository;
        private readonly IApplicationSettingsRepository _settingsRepository;
        private readonly ILogger<VerificationQueueStatusWorkflow> _logger;

        public VerificationQueueStatusWorkflow(
            ILibraryVerificationQueueService queue,
            IVerificationJobRepository jobRepository,
            IAudiobookRepository audiobookRepository,
            IApplicationSettingsRepository settingsRepository,
            ILogger<VerificationQueueStatusWorkflow> logger)
        {
            _queue = queue;
            _jobRepository = jobRepository;
            _audiobookRepository = audiobookRepository;
            _settingsRepository = settingsRepository;
            _logger = logger;
        }

        public async Task<IActionResult> StatusAsync(CancellationToken ct)
        {
            // --- Verification queue (in-memory snapshot + durable history) ---
            var jobs = _queue.SnapshotJobs();
            var queued = jobs.Where(j => j.Status == "Queued").ToList();
            var processing = jobs.FirstOrDefault(j => j.Status == "Processing");

            // Books still ahead of the worker: full counts for queued jobs,
            // remaining count for the running one. Whole-library jobs report
            // their resolved Total once processing; while still queued their
            // size is unknown and excluded (flagged via hasUnknownSizedJobs).
            var queuedBooks = 0;
            var hasUnknownSizedJobs = false;
            foreach (var job in queued)
            {
                if (job.AudiobookIds != null) queuedBooks += job.AudiobookIds.Count;
                else hasUnknownSizedJobs = true;
            }
            if (processing != null)
            {
                queuedBooks += Math.Max(0, processing.Total - processing.Processed);
            }

            var recentCompleted = await _jobRepository.GetRecentCompletedAsync(20, ct);
            var avgSecondsPerBook = VerificationEtaMath.ComputeAvgSecondsPerBook(recentCompleted);

            var completedToday = await _jobRepository.GetCompletedSinceAsync(DateTime.UtcNow.Date, ct);
            var completedBooksToday = completedToday
                .Sum(r => VerificationEtaMath.CountBooks(r.AudiobookIdsJson) ?? 0);

            // --- Series-catalog backfill progress ---
            var (cachedSeries, totalMultiBookSeries) = await ComputeSeriesBackfillProgressAsync(ct);

            return new OkObjectResult(new
            {
                verification = new
                {
                    queuedJobs = queued.Count,
                    queuedBooks,
                    hasUnknownSizedJobs,
                    processing = processing == null ? null : new
                    {
                        jobId = processing.Id,
                        audiobookId = processing.CurrentAudiobookId,
                        title = processing.CurrentAudiobookTitle,
                        processed = processing.Processed,
                        total = processing.Total
                    },
                    completedBooksToday,
                    avgSecondsPerBook = Math.Round(avgSecondsPerBook),
                    etaSeconds = queuedBooks > 0 ? (long?)Math.Round(queuedBooks * avgSecondsPerBook) : null
                },
                seriesBackfill = new
                {
                    cached = cachedSeries,
                    totalMultiBook = totalMultiBookSeries
                }
            });
        }

        /// <summary>
        /// How many multi-book series (2+ tracked records — the backfill
        /// service's own eligibility rule) already have a cached catalog.
        /// Mirrors LibrarySeriesHealthWorkflow's grouping so the two panels
        /// can never disagree about what counts as a series.
        /// </summary>
        private async Task<(int cached, int total)> ComputeSeriesBackfillProgressAsync(CancellationToken ct)
        {
            try
            {
                var settings = await _settingsRepository.GetAsync(ct);
                var region = string.IsNullOrWhiteSpace(settings?.DefaultSearchRegion)
                    ? "us"
                    : settings!.DefaultSearchRegion;

                var books = await _audiobookRepository.GetAllAsync();
                var memberships = await _audiobookRepository.GetAllSeriesMembershipsGroupedByAudiobookIdAsync(ct);

                var trackedCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var book in books)
                {
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    if (memberships.TryGetValue(book.Id, out var ms) && ms.Count > 0)
                    {
                        foreach (var m in ms)
                        {
                            var n = m.SeriesName?.Trim();
                            if (!string.IsNullOrWhiteSpace(n)) names.Add(n!);
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(book.Series))
                    {
                        names.Add(book.Series!.Trim());
                    }

                    foreach (var name in names)
                    {
                        trackedCounts[name] = trackedCounts.TryGetValue(name, out var c) ? c + 1 : 1;
                    }
                }

                var multiBook = trackedCounts.Where(kv => kv.Value >= 2).Select(kv => kv.Key).ToList();
                if (multiBook.Count == 0) return (0, 0);

                var totals = await _audiobookRepository.GetSeriesCatalogTotalsAsync(multiBook, region, ct);
                var cached = multiBook.Count(totals.ContainsKey);
                return (cached, multiBook.Count);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                // Progress display must never take the whole status endpoint down.
                _logger.LogDebug(ex, "Series backfill progress computation failed");
                return (0, 0);
            }
        }
    }
}
