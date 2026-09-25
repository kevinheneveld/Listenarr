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

namespace Listenarr.Api.Features.Verification
{
    public partial class VerificationController
    {
        /// <summary>
        /// Backfill the AI second opinion over stored verdicts the deterministic
        /// matcher could not settle (flagged as uncertain, or unverifiable) —
        /// re-reads the saved transcript, no re-transcription. Cursor-paged
        /// (afterId) and capped per call because each book is one slow model
        /// round-trip; the client loops with lastId. A confident match promotes
        /// the book to agent-verified, a confident mismatch flags it for review,
        /// anything else is recorded and left as it was.
        /// </summary>
        [HttpPost("ai-review")]
        public async Task<IActionResult> AiReview(
            [FromServices] VerificationAiReviewWorkflow workflow,
            [FromQuery] int limit = 3,
            [FromQuery] int afterId = 0,
            CancellationToken ct = default)
        {
            return await workflow.ReviewAsync(limit, afterId, ct);
        }

        /// <summary>How many stored verdicts still await the AI second opinion.</summary>
        [HttpGet("ai-review/pending")]
        public async Task<IActionResult> AiReviewPending(
            [FromServices] VerificationAiReviewWorkflow workflow,
            CancellationToken ct = default)
        {
            return await workflow.PendingAsync(ct);
        }
    }
}
