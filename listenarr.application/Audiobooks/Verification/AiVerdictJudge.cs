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
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Listenarr.Application.Audiobooks.Verification
{
    /// <summary>
    /// Prompt building and response parsing for the AI verdict review: when the
    /// deterministic matcher cannot settle a book, a language model reads the
    /// same whisper transcript against the stored metadata. The matcher fails
    /// on exactly the cases a reader finds trivial — a subtitle the credits
    /// never say ("Changes" vs "Changes: The Dresden Files, Book 12"), a series
    /// prefix ("Sector 64: Ambush"), a phonetically spelled surname ("Bobby A.
    /// Cartt" for Akart). Pure static functions so prompt and parsing are
    /// testable without a model.
    /// </summary>
    public static class AiVerdictJudge
    {
        public const int MaxTranscriptChars = 1200;

        public const string DecisionMatch = "match";
        public const string DecisionMismatch = "mismatch";
        public const string DecisionUnsure = "unsure";

        public static string BuildSystemPrompt() =>
            "You verify audiobook files for a library manager. " +
            "You are given the library's metadata for a book and a speech-to-text transcript of the first minutes " +
            "(and sometimes the last minutes) of the audio. Decide whether the audio IS that book. " +
            "Spoken credits usually name the publisher, the title, the author and the narrator; the transcript may " +
            "misspell names phonetically (\"Bobby A. Cartt\" for Bobby Akart, \"Ross Kultart\" for Ross Kolthart) — " +
            "treat a name that sounds like the expected one as the expected one. " +
            "The credits often omit a subtitle, series name or volume number that the library title carries, and often " +
            "add one the library title lacks; that is still the same book. " +
            "Answer \"match\" when the credits name this title (or a plausible rendering of it) together with this author " +
            "or narrator. Answer \"mismatch\" only when the credits clearly name a DIFFERENT title or a DIFFERENT author " +
            "that is not a misspelling of the expected one. " +
            "The narrator and publisher never decide: a different narrator or publisher means another edition of the SAME book, " +
            "which is a match when the title and author agree. " +
            "Only spoken credits count as evidence: do not use your own knowledge of the story to recognise a book from its narration. " +
            "An introduction, foreword, chapter heading or dedication is not the book's title — a transcript that opens with " +
            "\"An Introduction\" or a chapter and then names the expected author is the expected book. " +
            "Answer \"unsure\" when the transcript carries no credits (narration only, music, a foreign language) " +
            "or the evidence is too thin either way — never guess. " +
            "Respond with ONLY a JSON object of the form " +
            "{\"decision\":\"match\"|\"mismatch\"|\"unsure\",\"confidence\":<0..1>,\"reason\":\"<one short sentence quoting the credits>\"}. " +
            "No prose, no markdown.";

        public static string BuildUserPrompt(Audiobook audiobook, string transcript)
        {
            var sb = new StringBuilder();
            sb.Append("Library metadata:\n");
            sb.Append("  Title: ").Append(audiobook.Title ?? string.Empty).Append('\n');
            if (!string.IsNullOrWhiteSpace(audiobook.Subtitle))
            {
                sb.Append("  Subtitle: ").Append(audiobook.Subtitle).Append('\n');
            }
            if (audiobook.Authors is { Count: > 0 })
            {
                sb.Append("  Author(s): ").Append(string.Join(", ", audiobook.Authors)).Append('\n');
            }
            if (audiobook.Narrators is { Count: > 0 })
            {
                sb.Append("  Narrator(s) on record (other editions may differ): ").Append(string.Join(", ", audiobook.Narrators)).Append('\n');
            }
            if (!string.IsNullOrWhiteSpace(audiobook.Series))
            {
                sb.Append("  Series: ").Append(audiobook.Series);
                if (!string.IsNullOrWhiteSpace(audiobook.SeriesNumber))
                {
                    sb.Append(" #").Append(audiobook.SeriesNumber);
                }
                sb.Append('\n');
            }
            if (!string.IsNullOrWhiteSpace(audiobook.Publisher))
            {
                sb.Append("  Publisher: ").Append(audiobook.Publisher).Append('\n');
            }

            sb.Append("\nTranscript:\n");
            sb.Append(AiLibrarySweepJudge.Truncate(transcript, MaxTranscriptChars));
            sb.Append('\n');
            return sb.ToString();
        }

        /// <summary>
        /// Lenient parse: fences/prose around the object are tolerated, an
        /// unknown decision or a missing confidence yields null — which leaves
        /// the deterministic verdict untouched.
        /// </summary>
        public static VerificationAiReview? ParseResponse(string? responseText, string? model)
        {
            if (string.IsNullOrWhiteSpace(responseText)) return null;

            var start = responseText.IndexOf('{');
            var end = responseText.LastIndexOf('}');
            if (start < 0 || end <= start) return null;

            try
            {
                using var doc = JsonDocument.Parse(responseText[start..(end + 1)]);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return null;

                if (!root.TryGetProperty("decision", out var decisionEl) || decisionEl.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                var decision = decisionEl.GetString()?.Trim().ToLowerInvariant();
                if (decision is not (DecisionMatch or DecisionMismatch or DecisionUnsure)) return null;

                var confidence = 0.0;
                if (root.TryGetProperty("confidence", out var confidenceEl))
                {
                    if (confidenceEl.ValueKind == JsonValueKind.Number && confidenceEl.TryGetDouble(out var number))
                    {
                        confidence = number;
                    }
                    else if (confidenceEl.ValueKind == JsonValueKind.String
                        && double.TryParse(confidenceEl.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                    {
                        confidence = parsed;
                    }
                }
                // Models occasionally answer in percent.
                if (confidence > 1 && confidence <= 100) confidence /= 100;
                confidence = Math.Clamp(confidence, 0, 1);

                var reason = root.TryGetProperty("reason", out var reasonEl) && reasonEl.ValueKind == JsonValueKind.String
                    ? reasonEl.GetString()?.Trim()
                    : null;
                if (reason is { Length: > 300 }) reason = reason[..300];

                return new VerificationAiReview(decision, Math.Round(confidence, 3), string.IsNullOrEmpty(reason) ? null : reason, model);
            }
            catch (JsonException)
            {
                return null;
            }
        }
    }
}
