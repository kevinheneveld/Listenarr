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
        /// Queue a set of wanted audiobooks for automatic search. The server walks
        /// the list in the background, so the batch outlives the browser tab.
        /// Ids already pending or in flight are skipped.
        /// </summary>
        [HttpPost("wanted-queue")]
        [ProducesResponseType(typeof(WantedSearchEnqueueResponse), StatusCodes.Status202Accepted)]
        public ActionResult<WantedSearchEnqueueResponse> EnqueueWantedSearch([FromBody] WantedSearchQueueRequest request)
        {
            var ids = request?.AudiobookIds?.Where(id => id > 0).Distinct().ToList() ?? new List<int>();
            if (ids.Count == 0)
            {
                return BadRequest("At least one audiobookId is required.");
            }

            if (_wantedSearchQueue == null)
            {
                return StatusCode(StatusCodes.Status503ServiceUnavailable, "Wanted search is not available.");
            }

            var result = _wantedSearchQueue.Enqueue(ids);
            return Accepted(new WantedSearchEnqueueResponse
            {
                Accepted = result.Accepted,
                AlreadyQueued = result.AlreadyQueued,
                Snapshot = _wantedSearchQueue.Snapshot()
            });
        }

        /// <summary>Progress of the current (or last) wanted-search batch.</summary>
        [HttpGet("wanted-queue")]
        [ProducesResponseType(typeof(WantedSearchQueueSnapshot), StatusCodes.Status200OK)]
        public ActionResult<WantedSearchQueueSnapshot> GetWantedSearchQueue()
        {
            return Ok(_wantedSearchQueue?.Snapshot() ?? WantedSearchQueueSnapshot.Idle);
        }

        /// <summary>Drop the pending batch and abandon the book currently being searched.</summary>
        [HttpDelete("wanted-queue")]
        [ProducesResponseType(typeof(WantedSearchQueueSnapshot), StatusCodes.Status200OK)]
        public ActionResult<WantedSearchQueueSnapshot> CancelWantedSearch()
        {
            if (_wantedSearchQueue == null)
            {
                return Ok(WantedSearchQueueSnapshot.Idle);
            }

            _wantedSearchQueue.Cancel();
            return Ok(_wantedSearchQueue.Snapshot());
        }

        public sealed class WantedSearchQueueRequest
        {
            public List<int> AudiobookIds { get; set; } = new();
        }

        public sealed class WantedSearchEnqueueResponse
        {
            public int Accepted { get; set; }
            public int AlreadyQueued { get; set; }
            public WantedSearchQueueSnapshot Snapshot { get; set; } = WantedSearchQueueSnapshot.Idle;
        }
    }
}
