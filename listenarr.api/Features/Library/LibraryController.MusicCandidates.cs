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

namespace Listenarr.Api.Features.Library
{
    public partial class LibraryController
    {
        /// <summary>
        /// Read-only "smells like music" sweep over flagged/unverifiable books —
        /// file shape (many short tracks), music-dominated transcripts, and
        /// performer-style credits that don't match the record. Feeds the
        /// dashboard review list; nothing is modified here.
        /// </summary>
        /// <param name="ct">Cancellation token bound to the request.</param>
        [HttpGet("music-candidates")]
        public async Task<IActionResult> GetMusicCandidates(CancellationToken ct)
        {
            return await _musicCandidatesWorkflow.GetCandidatesAsync(ct);
        }
    }
}
