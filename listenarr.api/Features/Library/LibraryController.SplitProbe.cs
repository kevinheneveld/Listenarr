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
        /// Audio-probe candidates for Split Collection: the shape-derived
        /// boundary suspects (small stub files, encode shifts, whole-book
        /// files) worth transcribing when file names carry no information.
        /// </summary>
        /// <param name="id">Audiobook whose files should be analyzed.</param>
        /// <param name="workflow">Injected probe workflow.</param>
        /// <param name="ct">Cancellation token bound to the request.</param>
        [HttpGet("{id}/split/probe/candidates")]
        public async Task<IActionResult> GetSplitProbeCandidates(
            int id,
            [FromServices] LibrarySplitProbeWorkflow workflow,
            CancellationToken ct)
        {
            return await workflow.CandidatesAsync(id, ct);
        }

        /// <summary>
        /// Transcribe the opening of one tracked file (a ~40s whisper run —
        /// one file per request so the call stays inside reverse-proxy
        /// timeouts; the client loops candidates).
        /// </summary>
        /// <param name="id">Owning audiobook ID.</param>
        /// <param name="request">File to probe and optional window seconds.</param>
        /// <param name="workflow">Injected probe workflow.</param>
        /// <param name="ct">Cancellation token bound to the request.</param>
        [HttpPost("{id}/split/probe")]
        public async Task<IActionResult> ProbeSplitBoundary(
            int id,
            [FromBody] LibrarySplitProbeWorkflow.ProbeRequest request,
            [FromServices] LibrarySplitProbeWorkflow workflow,
            CancellationToken ct)
        {
            return await workflow.ProbeAsync(id, request, ct);
        }

        /// <summary>
        /// Build a proposed split plan from collected probe transcripts:
        /// groups open at spoken announcements/retail openers and after
        /// spoken epilogues, labeled from the announcements where possible.
        /// Read-only — applying goes through the same transfer/delete flow
        /// as the name-clustering preview.
        /// </summary>
        /// <param name="id">Audiobook being split.</param>
        /// <param name="request">Probe transcripts collected by the client.</param>
        /// <param name="workflow">Injected probe workflow.</param>
        /// <param name="ct">Cancellation token bound to the request.</param>
        [HttpPost("{id}/split/probe/plan")]
        public async Task<IActionResult> PlanSplitFromProbes(
            int id,
            [FromBody] LibrarySplitProbeWorkflow.ProbePlanRequest request,
            [FromServices] LibrarySplitProbeWorkflow workflow,
            CancellationToken ct)
        {
            return await workflow.PlanAsync(id, request, ct);
        }
    }
}
