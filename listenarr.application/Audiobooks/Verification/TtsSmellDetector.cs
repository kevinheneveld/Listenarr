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
using System.Text.RegularExpressions;

namespace Listenarr.Application.Audiobooks.Verification
{
    /// <summary>
    /// Heuristic "smells like a text-to-speech rip" scorer over verification
    /// artifacts already on the record (stored transcript, tracked file paths)
    /// — no new transcription. Companion to <see cref="MusicSmellDetector"/>
    /// with one crucial difference: TTS rips routinely PASS verification,
    /// because the synthetic announcement states the correct title and author
    /// (live case "Holy Island": "This audiobook was compiled by … Holy
    /// Island … by L. J. Ross" — every field matches, no human narrator on
    /// the files). So this detector also inspects agent-VERIFIED books; only
    /// human rulings (ManuallyVerified / Rejected) are exempt. Feeds the same
    /// human-confirmed wrong-content sweep, so weights are tuned for
    /// precision: every signal is announcement- or release-name-shaped
    /// compiler-speak that real publisher recordings never contain. Pure and
    /// deterministic for unit testing.
    /// </summary>
    public static class TtsSmellDetector
    {
        /// <summary>Score at or above which a book is surfaced for review.</summary>
        public const double CandidateThreshold = 0.5;

        public sealed record Result(double Score, IReadOnlyList<string> Reasons)
        {
            public bool IsCandidate => Score >= CandidateThreshold;
        }

        // Explicit synthesis vocabulary. Real audiobook credits never describe
        // their own production technology.
        private static readonly Regex TtsTechRegex = new(
            @"\btext[\s-]?to[\s-]?speech\b|\bspeech\s+synthes\w+|\bsynthes(?:ized|ised)\s+(?:voice|speech|narration)|\b(?:computer|machine|ai|artificial|synthetic)[\s-]generated\s+(?:voice|speech|narration|audio)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Compiler-speak announcements: publishers "present" a book "read by"
        // a narrator; TTS compilations are "compiled"/"generated"/"converted"
        // by someone, often name-dropping the cloud vendors whose voices they
        // used ("using technologies developed by Microsoft and Amazon").
        private static readonly Regex CompiledByRegex = new(
            @"\b(?:audio\s?book|audiobook|recording)\s+(?:was|has\s+been|is)\s+(?:compiled|generated|created|converted|produced)\s+(?:by|using|with|from)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex TechnologyVendorRegex = new(
            @"\busing\s+(?:the\s+)?technolog\w*\s+(?:developed|provided|created|offered)\s+by\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Distribution-group phrasing ("specifically for members of …") — the
        // way scene compilations sign their work. Soft: it describes the
        // release, not the narration, so it can't flag alone.
        private static readonly Regex ForMembersRegex = new(
            @"\b(?:specifically\s+)?for\s+(?:the\s+)?members\s+of\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Release-name tokens on tracked file paths: "TtS", "text-to-speech".
        // Boundary-guarded so words containing the letters can't match.
        private static readonly Regex PathTokenRegex = new(
            @"(?<![a-z0-9])(?:tts|text-to-speech|texttospeech)(?![a-z0-9])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Scores one book from its stored transcript and tracked file paths.
        /// </summary>
        public static Result Score(
            VerificationStatus status,
            string? transcript,
            IReadOnlyList<string?> filePaths)
        {
            // Any agent-written status is eligible — TTS rips usually VERIFY
            // (the synthetic announcement matches the metadata). Human rulings
            // outrank the heuristic in both directions.
            if (status is not (VerificationStatus.AgentVerified
                or VerificationStatus.AgentFlagged
                or VerificationStatus.AgentUnverifiable))
            {
                return new Result(0, Array.Empty<string>());
            }

            var reasons = new List<string>();
            double score = 0;

            if (!string.IsNullOrWhiteSpace(transcript))
            {
                if (TtsTechRegex.IsMatch(transcript))
                {
                    score += 0.6;
                    reasons.Add("audio announces text-to-speech / synthesized narration");
                }

                if (CompiledByRegex.IsMatch(transcript))
                {
                    score += 0.6;
                    reasons.Add("compiler-speak announcement (\"this audiobook was compiled by …\")");
                }

                if (TechnologyVendorRegex.IsMatch(transcript))
                {
                    score += 0.6;
                    reasons.Add("announcement credits voice technology vendors, not a narrator");
                }

                if (ForMembersRegex.IsMatch(transcript))
                {
                    score += 0.25;
                    reasons.Add("distribution-group announcement (\"for members of …\")");
                }
            }

            var pathHit = filePaths.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p) && PathTokenRegex.IsMatch(p!));
            if (pathHit != null)
            {
                score += 0.6;
                reasons.Add($"release/file name carries a TTS token ({Path.GetFileName(pathHit)})");
            }

            return new Result(Math.Min(1.0, score), reasons);
        }
    }
}
