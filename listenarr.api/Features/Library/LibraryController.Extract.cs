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

using Listenarr.Application.Audiobooks.Files;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library;

public partial class LibraryController
{
    /// <summary>
    /// Move a single tracked file out of its current audiobook and onto a destination
    /// audiobook constructed from the supplied Audible metadata. If an audiobook with
    /// the same ASIN already exists, the request must include a <c>duplicateStrategy</c>
    /// of "merge" or "duplicate"; otherwise a 409 is returned with the existing
    /// audiobook details so the caller can re-ask the user.
    /// </summary>
    /// <param name="audiobookId">Source audiobook ID.</param>
    /// <param name="fileId">AudiobookFile row ID to extract.</param>
    /// <param name="request">Destination metadata + duplicate strategy.</param>
    /// <param name="ct">Cancellation token bound to the request.</param>
    [HttpPost("{audiobookId}/files/{fileId}/extract")]
    public async Task<IActionResult> ExtractFileToNewAudiobook(
        int audiobookId,
        int fileId,
        [FromBody] ExtractFileRequest request,
        CancellationToken ct)
    {
        if (_fileExtractionService == null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { message = "File extraction service not available" });
        }

        if (request == null)
        {
            return BadRequest(new { message = "Extract request body is required." });
        }

        if (request.Metadata == null || string.IsNullOrWhiteSpace(request.Metadata.Title))
        {
            return BadRequest(new { message = "Destination metadata must include a title." });
        }

        var result = await _fileExtractionService.ExtractToNewAudiobookAsync(audiobookId, fileId, request, ct);
        if (result.Success)
        {
            return Ok(result);
        }

        if (result.Conflict != null && request.DuplicateStrategy == DuplicateStrategy.None)
        {
            return Conflict(result);
        }

        return BadRequest(result);
    }
}
