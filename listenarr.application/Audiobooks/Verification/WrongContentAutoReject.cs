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
    /// Runs the "Not an audiobook" flow (purge files, blocklist the delivering
    /// release, re-monitor, re-search) without a controller context — used by the
    /// verification background service to auto-reject confident wrong-content
    /// imports. Implemented in the API layer over the same workflow the manual
    /// button uses.
    /// </summary>
    public interface IWrongContentAutoRejector
    {
        Task<bool> TryRejectAsync(int audiobookId, CancellationToken ct = default);
    }

    /// <summary>
    /// Decision predicate for auto-rejecting a book based on its verification
    /// verdict. Deliberately the highest-precision case ONLY: the audio must
    /// ANNOUNCE a different book (credit evidence), not merely fail to match.
    /// NoSpokenCredits and Uncertain never auto-reject — absence of evidence is
    /// a human's call.
    /// </summary>
    public static class WrongContentAutoReject
    {
        /// <summary>Minimum verdict confidence for an automatic rejection.</summary>
        public const double MinConfidence = 0.9;

        public static bool ShouldAutoReject(
            bool settingEnabled,
            string? trigger,
            VerificationOutcome outcome,
            double? confidence,
            string? heardTitle,
            string? heardAuthor)
        {
            if (!settingEnabled) return false;

            // Fresh imports and explicit user-requested sweeps (manual / batch /
            // Re-check inconclusive) qualify: the evidence bar below is identical
            // either way. Excluded on purpose:
            //  - Transfer: files just moved onto the book by a human mid-workflow —
            //    those deserve eyes, not deletion.
            //  - Metadata: the user just changed the book's identity; a mismatch
            //    there more likely means the edit was wrong than the files are junk.
            var qualifying =
                string.Equals(trigger, VerificationTriggers.Import, StringComparison.OrdinalIgnoreCase)
                || string.Equals(trigger, VerificationTriggers.Manual, StringComparison.OrdinalIgnoreCase);
            if (!qualifying) return false;

            if (outcome != VerificationOutcome.Mismatch) return false;

            if ((confidence ?? 0) < MinConfidence) return false;

            // Credit evidence required: the transcript must actually claim a
            // different title or author, not merely lack ours.
            var hasEvidence = !string.IsNullOrWhiteSpace(heardTitle) || !string.IsNullOrWhiteSpace(heardAuthor);
            return hasEvidence;
        }
    }
}
