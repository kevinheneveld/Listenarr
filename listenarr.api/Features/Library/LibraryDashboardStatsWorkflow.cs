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

using Listenarr.Application.Audiobooks.Catalog;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    /// <summary>
    /// Dashboard stats: quality distribution (codec / bitrate buckets over
    /// tracked files) and metadata-completeness gap counts. The same
    /// per-field predicate drives the counts and the missing/{field}/ids
    /// drill-down, so the dashboard and its click-throughs can never disagree.
    /// </summary>
    public sealed class LibraryDashboardStatsWorkflow
    {
        private readonly IAudiobookRepository _repo;
        private readonly IAudiobookFileRepository _fileRepository;
        private readonly DashboardAggregateCache _aggregateCache;

        public LibraryDashboardStatsWorkflow(IAudiobookRepository repo, IAudiobookFileRepository fileRepository, DashboardAggregateCache aggregateCache)
        {
            _repo = repo;
            _fileRepository = fileRepository;
            _aggregateCache = aggregateCache;
        }

        // Bitrate is stored in bits/sec; buckets are expressed in kbps.
        private static readonly (string Label, Func<int?, bool> Matches)[] BitrateBuckets =
        {
            ("Unknown", b => !b.HasValue || b.Value <= 0),
            ("< 64 kbps", b => b.HasValue && b.Value > 0 && b.Value / 1000 < 64),
            ("64–128 kbps", b => b.HasValue && b.Value / 1000 >= 64 && b.Value / 1000 < 128),
            ("128–192 kbps", b => b.HasValue && b.Value / 1000 >= 128 && b.Value / 1000 < 192),
            ("192–256 kbps", b => b.HasValue && b.Value / 1000 >= 192 && b.Value / 1000 < 256),
            ("256–320 kbps", b => b.HasValue && b.Value / 1000 >= 256 && b.Value / 1000 < 320),
            ("320+ kbps", b => b.HasValue && b.Value / 1000 >= 320),
        };

        public async Task<IActionResult> GetStatsAsync(CancellationToken ct)
        {
            // Full-library aggregate; memoized ~5 min (see DashboardAggregateCache).
            var payload = await _aggregateCache.GetOrCreateAsync<object>("dashboard-stats", () => ComputeStatsPayloadAsync(ct));
            return new OkObjectResult(payload);
        }

        private async Task<object> ComputeStatsPayloadAsync(CancellationToken ct)
        {
            var files = await _fileRepository.GetFormatSummariesAsync(ct);
            var byCodec = files
                .GroupBy(f => string.IsNullOrWhiteSpace(f.Codec) ? "Unknown" : f.Codec!.ToLowerInvariant())
                .Select(g => new { codec = g.Key, count = g.Count() })
                .OrderByDescending(c => c.count)
                .ToList();
            var byBitrate = BitrateBuckets
                .Select(bucket => new { label = bucket.Label, count = files.Count(f => bucket.Matches(f.Bitrate)) })
                .Where(b => b.count > 0)
                .ToList();

            var books = await _repo.GetAllAsync();
            var completeness = new
            {
                totalBooks = books.Count,
                missingCoverArt = books.Count(b => LibraryMetadataGaps.MissesField(b, MissingField.CoverArt)),
                missingDescription = books.Count(b => LibraryMetadataGaps.MissesField(b, MissingField.Description)),
                missingNarrators = books.Count(b => LibraryMetadataGaps.MissesField(b, MissingField.Narrators)),
                missingSeriesPosition = books.Count(b => LibraryMetadataGaps.MissesField(b, MissingField.SeriesPosition)),
            };

            return new { quality = new { byCodec, byBitrate }, completeness };
        }

        public async Task<IActionResult> GetMissingIdsAsync(string field, CancellationToken ct)
        {
            if (!Enum.TryParse<MissingField>(field, ignoreCase: true, out var parsed))
            {
                return new BadRequestObjectResult(new { message = $"Unknown field '{field}'" });
            }

            var ids = await _aggregateCache.GetOrCreateAsync($"dashboard-missing:{parsed}", async () =>
            {
                var books = await _repo.GetAllAsync();
                return books.Where(b => LibraryMetadataGaps.MissesField(b, parsed)).Select(b => b.Id).ToList();
            });
            return new OkObjectResult(new { field = parsed.ToString(), ids });
        }
    }
}
