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
        /// Manual import: upload audio files (or a zip of them) directly into
        /// this audiobook's library folder — for purchases acquired outside
        /// the download pipeline. Files are stored under the book's folder
        /// and registered as AudiobookFile records.
        /// </summary>
        /// <param name="id">Target audiobook ID.</param>
        /// <param name="files">Uploaded audio files or zip archives.</param>
        /// <param name="workflow">Injected upload workflow.</param>
        /// <param name="ct">Cancellation token bound to the request.</param>
        [HttpPost("{id}/files/upload")]
        [DisableRequestSizeLimit]
        [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue, ValueLengthLimit = int.MaxValue)]
        public async Task<IActionResult> UploadAudiobookFiles(
            int id,
            [FromForm] IFormFileCollection files,
            [FromServices] LibraryUploadWorkflow workflow,
            CancellationToken ct)
        {
            return await workflow.UploadAsync(id, files, ct);
        }
    }
}
