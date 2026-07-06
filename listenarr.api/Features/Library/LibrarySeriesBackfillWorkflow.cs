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

using Listenarr.Infrastructure.HostedServices.Catalog;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    /// <summary>
    /// One-shot series-catalog backfill — the dashboard's "Backfill now"
    /// button. Runs a single pass of the periodic backfill processor with a
    /// higher fetch cap so the user can skip the multi-day 20-per-6h ramp.
    /// The 2s-per-fetch pacing toward Audible is preserved.
    /// </summary>
    public sealed class LibrarySeriesBackfillWorkflow
    {
        /// <summary>Manual runs may fetch more per pass than the background cycle's 20.</summary>
        public const int ManualRunMaxFetches = 100;

        private readonly ISeriesCatalogBackfillProcessor? _processor;
        private readonly ILogger<LibrarySeriesBackfillWorkflow> _logger;

        public LibrarySeriesBackfillWorkflow(
            ILogger<LibrarySeriesBackfillWorkflow> logger,
            ISeriesCatalogBackfillProcessor? processor = null)
        {
            _logger = logger;
            _processor = processor;
        }

        public async Task<IActionResult> RunAsync(CancellationToken ct)
        {
            if (_processor == null)
            {
                return new ObjectResult(new { message = "Series catalog backfill is not available" })
                {
                    StatusCode = StatusCodes.Status503ServiceUnavailable
                };
            }

            _logger.LogInformation("Manual series-catalog backfill requested (cap {Cap})", ManualRunMaxFetches);
            var result = await _processor.RunOnceAsync(ManualRunMaxFetches, ct);

            return new OkObjectResult(new
            {
                message = $"Backfill pass complete: fetched {result.Fetched} series catalog(s), {result.AlreadyCached} already cached, {result.Remaining} remaining",
                totalSeries = result.TotalSeries,
                alreadyCached = result.AlreadyCached,
                fetched = result.Fetched,
                failed = result.Failed,
                remaining = result.Remaining
            });
        }
    }
}
