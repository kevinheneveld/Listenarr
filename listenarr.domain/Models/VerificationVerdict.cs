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

using Listenarr.Domain.Models.Enumerations;

namespace Listenarr.Domain.Models
{
    /// <summary>
    /// Score and evidence for a single metadata field matched against the
    /// spoken-credits transcript (ADR-0001).
    /// </summary>
    /// <param name="Score">Match strength in [0, 1].</param>
    /// <param name="MatchedText">The transcript fragment that produced the score, for audit/triage UI; null when nothing usable was found.</param>
    public record VerificationFieldMatch(double Score, string? MatchedText);

    /// <summary>
    /// Result of an identity-verification pass over one audiobook (ADR-0001).
    /// Per-field results are kept separate (not folded into one confidence blob)
    /// so the triage UI can show which field diverged and thresholds can be
    /// tuned per field. A null field means it was not evaluated (e.g. the book
    /// has no stored narrator).
    /// </summary>
    public record VerificationVerdict
    {
        public required VerificationOutcome Outcome { get; init; }

        /// <summary>Aggregate confidence in the outcome, in [0, 1].</summary>
        public required double Confidence { get; init; }

        /// <summary>How the verdict was produced, e.g. "deterministic" or "llm:ollama:llama3".</summary>
        public required string Method { get; init; }

        public VerificationFieldMatch? TitleMatch { get; init; }
        public VerificationFieldMatch? AuthorMatch { get; init; }
        public VerificationFieldMatch? NarratorMatch { get; init; }
        public VerificationFieldMatch? PublisherMatch { get; init; }

        /// <summary>What STT heard (sampled windows), persisted for audit.</summary>
        public string? Transcript { get; init; }
    }
}
