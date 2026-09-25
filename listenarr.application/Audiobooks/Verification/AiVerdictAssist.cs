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
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Verification
{
    /// <summary>
    /// AI second opinion on verdicts the deterministic matcher could not settle.
    /// Live picture at introduction: 203 books sat in "uncertain" with credits
    /// whisper had transcribed perfectly well — the loss was in interpretation
    /// (subtitles the credits skip, series prefixes, phonetic surnames), not in
    /// transcription. The model reads the same transcript and may promote an
    /// Uncertain/NoSpokenCredits verdict to Match, or turn it into a Mismatch
    /// flagged for review. Fails closed to the deterministic verdict: disabled,
    /// unconfigured, unreachable or a garbage answer changes nothing. It never
    /// rejects or deletes — an AI mismatch is capped below the auto-reject bar.
    /// </summary>
    public sealed class AiVerdictAssist
    {
        /// <summary>The model must be at least this sure before its answer changes the outcome.</summary>
        public const double MinDecisiveConfidence = 0.8;

        /// <summary>Ceiling for an AI-promoted Match — the deterministic "author loud and clear" path caps there too.</summary>
        public const double MaxAiMatchConfidence = 0.85;

        /// <summary>
        /// Ceiling for an AI-driven Mismatch. Strictly below
        /// <see cref="WrongContentAutoReject.MinConfidence"/> so a model's
        /// reading of a transcript can flag a book for a human but never
        /// trigger the purge-and-blocklist flow on its own.
        /// </summary>
        public const double MaxAiMismatchConfidence = 0.85;

        private readonly IAiAssistService _aiAssist;
        private readonly IConfigurationService _configurationService;
        private readonly ILogger<AiVerdictAssist> _logger;

        public AiVerdictAssist(IAiAssistService aiAssist, IConfigurationService configurationService, ILogger<AiVerdictAssist> logger)
        {
            _aiAssist = aiAssist;
            _configurationService = configurationService;
            _logger = logger;
        }

        /// <summary>Only the verdicts the matcher gave up on are worth a model call.</summary>
        public static bool ShouldReview(VerificationVerdict verdict) =>
            verdict.Outcome is VerificationOutcome.Uncertain or VerificationOutcome.NoSpokenCredits
            && !string.IsNullOrWhiteSpace(verdict.Transcript)
            && !verdict.Transcript.StartsWith("[inconclusive:", StringComparison.Ordinal);

        /// <summary>
        /// Returns the verdict with the model's review folded in, or the input
        /// unchanged when the review is off, unavailable, or indecisive.
        /// </summary>
        public async Task<VerificationVerdict> ReviewAsync(Audiobook audiobook, VerificationVerdict verdict, CancellationToken ct = default)
        {
            if (!ShouldReview(verdict)) return verdict;

            try
            {
                var settings = await _configurationService.GetApplicationSettingsAsync();
                if (!settings.AiAssistReviewVerifications || !await _aiAssist.IsConfiguredAsync(ct))
                {
                    return verdict;
                }

                var raw = await _aiAssist.CompleteJsonAsync(
                    AiVerdictJudge.BuildSystemPrompt(),
                    AiVerdictJudge.BuildUserPrompt(audiobook, verdict.Transcript!),
                    ct);
                var review = AiVerdictJudge.ParseResponse(raw, settings.AiAssistModel);
                if (review == null)
                {
                    _logger.LogDebug("AI verdict review for audiobook {Id} returned nothing usable", audiobook.Id);
                    return verdict;
                }

                var reviewed = Apply(verdict, review);
                _logger.LogInformation(
                    "AI verdict review for audiobook {Id} ('{Title}'): {Decision} ({Confidence:0.00}) — {Reason}; outcome {Before} → {After}",
                    audiobook.Id, LogRedaction.SanitizeText(audiobook.Title), review.Decision, review.Confidence,
                    LogRedaction.SanitizeText(review.Reason), verdict.Outcome, reviewed.Outcome);
                return reviewed;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "AI verdict review failed for audiobook {Id}; keeping the deterministic verdict", audiobook.Id);
                return verdict;
            }
        }

        /// <summary>
        /// The promotion rules, pure so they are directly unit-testable: a
        /// confident "match" becomes a Match capped at
        /// <see cref="MaxAiMatchConfidence"/>; a confident "mismatch" becomes a
        /// Mismatch capped at <see cref="MaxAiMismatchConfidence"/> (review, not
        /// rejection); anything else keeps the outcome and only records the
        /// review. The method string gains an "+ai-review" suffix when the
        /// outcome changed so the audit trail shows who decided.
        /// </summary>
        public static VerificationVerdict Apply(VerificationVerdict verdict, VerificationAiReview review)
        {
            var decisive = review.Confidence >= MinDecisiveConfidence;
            if (decisive && review.Decision == AiVerdictJudge.DecisionMatch)
            {
                return verdict with
                {
                    Outcome = VerificationOutcome.Match,
                    Confidence = Math.Round(Math.Min(MaxAiMatchConfidence, review.Confidence), 3),
                    Method = verdict.Method + "+ai-review",
                    AiReview = review
                };
            }

            if (decisive && review.Decision == AiVerdictJudge.DecisionMismatch)
            {
                return verdict with
                {
                    Outcome = VerificationOutcome.Mismatch,
                    Confidence = Math.Round(Math.Min(MaxAiMismatchConfidence, review.Confidence), 3),
                    Method = verdict.Method + "+ai-review",
                    AiReview = review
                };
            }

            return verdict with { AiReview = review };
        }
    }
}
