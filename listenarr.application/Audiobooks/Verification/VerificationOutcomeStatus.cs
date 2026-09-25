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
    /// The one mapping from a verifier outcome to the persisted status, shared
    /// by the verification worker and the AI review backfill.
    /// </summary>
    public static class VerificationOutcomeStatus
    {
        public static VerificationStatus For(VerificationOutcome outcome) => outcome switch
        {
            VerificationOutcome.Match => VerificationStatus.AgentVerified,
            // No credit-shaped claims at all: neutral, NOT a flag — absence of
            // credits is not evidence of wrong content.
            VerificationOutcome.NoSpokenCredits => VerificationStatus.AgentUnverifiable,
            // Mismatch AND Uncertain both need a human eye; the persisted detail
            // JSON distinguishes them in the triage UI.
            _ => VerificationStatus.AgentFlagged
        };
    }
}
