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
using System.Text;
using System.Text.Json;

namespace Listenarr.Application.Search
{
    /// <summary>
    /// Prompt building and response parsing for the AI release gate: before an
    /// automatic search grabs its top-scored release, a language model checks
    /// the shortlisted release titles against the target book and names the
    /// ones that are clearly something else (a music album, a different book,
    /// an e-book pack). Deterministic filters already reject most junk by
    /// category and token overlap; this catches what slips through — scene-
    /// tagged music names, series-adjacent wrong books.
    ///
    /// Failing open is the contract: an unreachable endpoint or garbage answer
    /// gates nothing, and a wrongly-gated release only delays acquisition until
    /// the next search cycle — the gate never deletes or blocks anything
    /// permanently. Pure static functions so prompt and parsing are testable.
    /// </summary>
    public static class AiReleaseGateJudge
    {
        public const int MaxCandidates = 5;

        public sealed record ReleaseInput(int Index, string Title, long? SizeBytes);
        public sealed record GateVerdict(int Index, string Reason);

        public static string BuildSystemPrompt() =>
            "You screen download candidates for an audiobook manager. " +
            "Given the target audiobook and a numbered list of release names from indexers, " +
            "identify releases that are clearly NOT an audiobook copy of the target: music albums or " +
            "discographies, movies or TV, e-book-only packs, or a different book entirely. " +
            "Release names use scene conventions (dots for spaces, quality tags, group suffixes) — that is normal, not suspicious. " +
            "File format, bitrate, and size are NEVER reasons to flag. " +
            "Most shortlists contain no wrong releases: an empty list is the common correct answer. " +
            "Only flag a release when you are confident; when unsure, do not flag it. " +
            "Respond with ONLY a JSON object of the form " +
            "{\"reject\":[{\"index\":<number>,\"reason\":\"<short reason>\"}]} — an empty list when nothing is clearly wrong. No prose, no markdown.";

        public static string BuildUserPrompt(
            string title,
            IReadOnlyList<string> authors,
            int? runtimeMinutes,
            IReadOnlyList<ReleaseInput> releases)
        {
            var sb = new StringBuilder();
            sb.Append("Target audiobook: \"").Append(title).Append('"');
            if (authors.Count > 0) sb.Append(" by ").Append(string.Join(", ", authors));
            if (runtimeMinutes is > 0) sb.Append(" (~").Append(runtimeMinutes).Append(" minutes)");
            sb.AppendLine();

            sb.AppendLine("Candidate releases:");
            foreach (var release in releases.Take(MaxCandidates))
            {
                sb.Append(release.Index).Append(": ").Append(release.Title);
                if (release.SizeBytes is > 0)
                {
                    sb.Append(" [").Append(release.SizeBytes.Value / (1024 * 1024)).Append(" MB]");
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }

        /// <summary>
        /// Lenient parse mirroring SplitSuggestionAiRefiner: fences/prose are
        /// tolerated, unknown indexes are dropped, and any structural garbage
        /// yields an empty list — which fails open (nothing gets gated).
        /// </summary>
        public static List<GateVerdict> ParseResponse(string? responseText, IReadOnlySet<int> validIndexes)
        {
            var verdicts = new List<GateVerdict>();
            if (string.IsNullOrWhiteSpace(responseText)) return verdicts;

            var start = responseText.IndexOf('{');
            var end = responseText.LastIndexOf('}');
            if (start < 0 || end <= start) return verdicts;

            try
            {
                using var doc = JsonDocument.Parse(responseText[start..(end + 1)]);
                if (!doc.RootElement.TryGetProperty("reject", out var reject)
                    || reject.ValueKind != JsonValueKind.Array)
                {
                    return verdicts;
                }

                foreach (var item in reject.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (!item.TryGetProperty("index", out var indexEl)
                        || indexEl.ValueKind != JsonValueKind.Number
                        || !indexEl.TryGetInt32(out var index)
                        || !validIndexes.Contains(index))
                    {
                        continue;
                    }

                    var reason = item.TryGetProperty("reason", out var reasonEl) && reasonEl.ValueKind == JsonValueKind.String
                        ? reasonEl.GetString() ?? "flagged"
                        : "flagged";
                    verdicts.Add(new GateVerdict(index, reason));
                }
            }
            catch (JsonException)
            {
                verdicts.Clear();
            }

            return verdicts;
        }
    }
}
