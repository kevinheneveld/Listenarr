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

using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Search
{
    public partial class SearchController
    {
        /// <summary>
        /// On-demand "Search now" for a set of audiobooks (e.g. every owned, monitored book in a
        /// series). Runs the same per-book automatic-search logic as the background cycle — so it
        /// skips books already meeting the quality cutoff or with an active download, and only grabs
        /// genuine upgrades — while bypassing the sweep throttle. Returns a per-book summary.
        /// </summary>
        [HttpPost("now")]
        [ProducesResponseType(typeof(SearchNowResponse), StatusCodes.Status200OK)]
        public async Task<ActionResult<SearchNowResponse>> SearchNow([FromBody] SearchNowRequest request, CancellationToken ct)
        {
            if (request?.AudiobookIds == null || request.AudiobookIds.Count == 0)
            {
                return BadRequest("At least one audiobookId is required.");
            }

            if (_automaticSearchInvoker == null)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, "Automatic search is not available.");
            }

            // De-dupe and cap to protect indexers from an accidental library-wide sweep.
            const int MaxBooksPerRequest = 100;
            var ids = request.AudiobookIds.Where(id => id > 0).Distinct().Take(MaxBooksPerRequest).ToList();

            var results = new List<AutomaticSearchBookResult>(ids.Count);
            foreach (var id in ids)
            {
                ct.ThrowIfCancellationRequested();
                results.Add(await _automaticSearchInvoker.SearchAudiobookNowAsync(id, ct));
            }

            return Ok(new SearchNowResponse
            {
                Requested = ids.Count,
                Queued = results.Count(r => r.DownloadsQueued > 0),
                Skipped = results.Count(r => r.Success && r.DownloadsQueued == 0),
                Failed = results.Count(r => !r.Success),
                Results = results
            });
        }

        public sealed class SearchNowRequest
        {
            public List<int> AudiobookIds { get; set; } = new();
        }

        public sealed class SearchNowResponse
        {
            public int Requested { get; set; }
            public int Queued { get; set; }
            public int Skipped { get; set; }
            public int Failed { get; set; }
            public List<AutomaticSearchBookResult> Results { get; set; } = new();
        }
    }
}
