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
using Listenarr.Domain.Audiobooks;
using Listenarr.Domain.Audiobooks.Enumerations;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Verification
{
    /// <summary>
    /// AI verdict review backfill: walks agent-judged books whose stored verdict
    /// is Uncertain or NoSpokenCredits and has no AI review yet, and asks the
    /// model to read the saved transcript against the metadata. Live picture at
    /// introduction: 203 uncertain books whose credits whisper had transcribed
    /// clearly. One model call per book (~10 s on a local 9B model), so each
    /// HTTP call handles a few books and the client loops with the cursor —
    /// the same shape as the AI library sweep, for the same reverse-proxy
    /// timeout reason.
    /// </summary>
    public sealed class VerificationAiReviewWorkflow
    {
        private const int DefaultLimit = 3;
        private const int MaxLimit = 10;

        private readonly IAudiobookRepository _repo;
        private readonly IAiAssistService _aiAssist;
        private readonly IConfigurationService _configurationService;
        private readonly AiVerdictAssist _assist;
        private readonly ILogger<VerificationAiReviewWorkflow> _logger;

        public VerificationAiReviewWorkflow(
            IAudiobookRepository repo,
            IAiAssistService aiAssist,
            IConfigurationService configurationService,
            AiVerdictAssist assist,
            ILogger<VerificationAiReviewWorkflow> logger)
        {
            _repo = repo;
            _aiAssist = aiAssist;
            _configurationService = configurationService;
            _assist = assist;
            _logger = logger;
        }

        public async Task<IActionResult> PendingAsync(CancellationToken ct)
        {
            var ids = await _repo.GetAiReviewCandidateIdsAsync(0, int.MaxValue, ct);
            return new OkObjectResult(new { pending = ids.Count });
        }

        public async Task<IActionResult> ReviewAsync(int limit, int afterId, CancellationToken ct)
        {
            var settings = await _configurationService.GetApplicationSettingsAsync();
            if (!settings.AiAssistReviewVerifications || !await _aiAssist.IsConfiguredAsync(ct))
            {
                return new BadRequestObjectResult(new
                {
                    message = "AI assist is not configured, or reviewing verifications is switched off — see Settings → AI Assist."
                });
            }

            limit = Math.Clamp(limit <= 0 ? DefaultLimit : limit, 1, MaxLimit);
            var ids = await _repo.GetAiReviewCandidateIdsAsync(afterId, limit, ct);
            if (ids.Count == 0)
            {
                return new OkObjectResult(new AiReviewBatchResult(0, null, true, 0, 0, 0, Array.Empty<AiReviewChange>()));
            }

            var reviewed = 0;
            var promoted = 0;
            var flagged = 0;
            var changes = new List<AiReviewChange>();
            int? lastId = null;

            foreach (var id in ids)
            {
                ct.ThrowIfCancellationRequested();

                var audiobook = await _repo.GetByIdAsync(id);
                if (audiobook == null || !audiobook.VerificationStatus.IsAgentWritable())
                {
                    lastId = id;
                    continue;
                }

                var stored = VerificationDetailSerializer.TryDeserialize(audiobook.VerificationDetailJson);
                if (stored == null)
                {
                    lastId = id;
                    continue;
                }

                var verdict = stored with { Transcript = audiobook.VerificationTranscript };
                var result = await _assist.ReviewAsync(audiobook, verdict, ct);
                if (result.AiReview == null)
                {
                    // Nothing came back (endpoint down, garbage answer): stop the
                    // batch here so the client doesn't burn through the cursor
                    // while the model is unreachable. Not advancing lastId keeps
                    // this book first in line next time.
                    _logger.LogWarning("AI verdict review produced no answer for audiobook {Id}; ending the batch", id);
                    break;
                }

                ApplyReview(audiobook, result);
                await _repo.UpdateAsync(audiobook);

                reviewed++;
                lastId = id;
                if (result.Outcome != verdict.Outcome)
                {
                    if (result.Outcome == VerificationOutcome.Match) promoted++;
                    else if (result.Outcome == VerificationOutcome.Mismatch) flagged++;
                    changes.Add(new AiReviewChange(
                        audiobook.Id,
                        audiobook.Title ?? $"id {audiobook.Id}",
                        verdict.Outcome.ToString(),
                        result.Outcome.ToString(),
                        result.AiReview.Reason));
                }
            }

            var exhausted = lastId == null ? false : ids.Count < limit;
            return new OkObjectResult(new AiReviewBatchResult(reviewed, lastId, exhausted, promoted, flagged, reviewed - promoted - flagged, changes));
        }

        /// <summary>
        /// Stamp the reviewed verdict onto the entity. Unlike the worker's
        /// ApplyVerdict this keeps VerifiedBy/VerifiedAt from the whisper pass
        /// — the audio was not re-listened to, only re-read.
        /// </summary>
        public static void ApplyReview(Audiobook audiobook, VerificationVerdict reviewed)
        {
            audiobook.VerificationStatus = VerificationOutcomeStatus.For(reviewed.Outcome);
            audiobook.VerificationConfidence = reviewed.Confidence;
            audiobook.VerificationMethod = reviewed.Method;
            audiobook.VerificationDetailJson = VerificationDetailSerializer.Serialize(reviewed);
        }

        public sealed record AiReviewChange(int AudiobookId, string Title, string From, string To, string? Reason);

        public sealed record AiReviewBatchResult(
            int ReviewedCount,
            int? LastId,
            bool Exhausted,
            int Promoted,
            int Flagged,
            int Unchanged,
            IReadOnlyList<AiReviewChange> Changes);
    }
}
