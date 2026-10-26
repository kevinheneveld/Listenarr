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


namespace Listenarr.Application.Audiobooks.Verification
{
    /// <summary>
    /// Choosing between the first-pass and escalated readings of the same
    /// sample windows. The larger model is not uniformly better: on a Recorded
    /// Books title base.en heard the announcer over the intro music and the
    /// closing credits while small.en collapsed both to narration, and the old
    /// "escalated text wins" rule threw the credits away.
    /// </summary>
    public partial class DeterministicIdentityVerifier
    {
        /// <summary>
        /// One scored combination of first-pass / escalated window texts.
        /// </summary>
        public sealed record VerdictCandidate(VerificationVerdict Verdict, string? OpeningText, string? ClosingText, bool UsesEscalatedText);

        /// <summary>
        /// Picks the verdict carrying the most credit evidence: a decided
        /// outcome (Match or Mismatch) beats anything inconclusive; among
        /// inconclusive verdicts one whose transcript has announcement-shaped
        /// text or an extractable heard credit beats one with none; then higher
        /// confidence; the escalated (larger-model) text breaks remaining ties,
        /// and decides outright between two decided outcomes. Public + pure so
        /// the ranking is directly unit-testable.
        /// </summary>
        public static VerdictCandidate ChooseBestVerdict(IReadOnlyList<VerdictCandidate> candidates)
        {
            if (candidates.Count == 0) throw new ArgumentException("at least one candidate is required", nameof(candidates));

            VerdictCandidate? best = null;
            var bestRank = -1;
            foreach (var candidate in candidates)
            {
                var rank = EvidenceRank(candidate.Verdict);
                if (best == null || Beats(candidate, rank, best, bestRank))
                {
                    best = candidate;
                    bestRank = rank;
                }
            }
            return best!;

            static bool Beats(VerdictCandidate a, int rankA, VerdictCandidate b, int rankB)
            {
                if (rankA != rankB) return rankA > rankB;
                if (rankA == DecidedRank)
                {
                    // Two decided readings of the same audio disagree or agree:
                    // trust the larger model, then confidence.
                    if (a.UsesEscalatedText != b.UsesEscalatedText) return a.UsesEscalatedText;
                    return a.Verdict.Confidence > b.Verdict.Confidence;
                }
                if (a.Verdict.Confidence != b.Verdict.Confidence) return a.Verdict.Confidence > b.Verdict.Confidence;
                return a.UsesEscalatedText && !b.UsesEscalatedText;
            }
        }

        private const int DecidedRank = 2;

        private static int EvidenceRank(VerificationVerdict verdict)
        {
            if (verdict.Outcome is VerificationOutcome.Match or VerificationOutcome.Mismatch) return DecidedRank;
            return AnnouncesSomething(verdict.Transcript) ? 1 : 0;
        }

        /// <summary>
        /// Distinct texts available for one window: the escalated reading (when
        /// any) and the first-pass reading (when any and different). A window
        /// with no text at all yields a single null option so the other window
        /// can still be scored.
        /// </summary>
        public static IEnumerable<(string? Text, bool Escalated)> WindowOptions(string? firstPass, string? escalated)
        {
            if (escalated != null) yield return (escalated, true);
            if (firstPass != null && !string.Equals(firstPass, escalated, StringComparison.Ordinal)) yield return (firstPass, false);
            if (firstPass == null && escalated == null) yield return (null, false);
        }
    }
}
