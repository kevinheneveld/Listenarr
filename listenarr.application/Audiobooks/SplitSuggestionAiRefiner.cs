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

namespace Listenarr.Application.Audiobooks
{
    /// <summary>
    /// Prompt building and response parsing for the AI-assisted pass over
    /// split-collection destination suggestions. The deterministic
    /// <see cref="SplitDestinationSuggester"/> only substring-matches titles,
    /// which misses renames ("Rama" → the record titled "Rendezvous with
    /// Rama") and produces junk on generic words ("Space Trilogy" → "Space");
    /// a language model handles exactly that fuzzy step. Pure static
    /// functions — the HTTP round-trip lives behind IAiAssistService — so the
    /// prompt shape and the lenient parsing are unit-testable.
    ///
    /// Suggestions are advisory: the user confirms every group in the modal,
    /// so a wrong model answer costs a click, never a file.
    /// </summary>
    public static class SplitSuggestionAiRefiner
    {
        public sealed record ClusterInput(string Key, string DisplayName, IReadOnlyList<string> SampleFileNames, int? CurrentSuggestionId);
        public sealed record CandidateInput(int Id, string Title);

        // Keep prompts bounded on huge libraries: same-author candidates all
        // fit comfortably; whole-library fallback gets capped.
        public const int MaxCandidates = 150;
        public const int MaxSampleFileNames = 3;

        public static string BuildSystemPrompt() =>
            "You match groups of audiobook files to existing library records. " +
            "Each group has a name (usually a folder or filename stem) and sample file names. " +
            "Pick the library record each group's audio belongs to, judging by book identity — " +
            "a group may use a series name, a subtitle, an abbreviation, or a partial title of its record. " +
            "Only match when you are confident it is the same book; otherwise use null. " +
            "Respond with ONLY a JSON object of the form " +
            "{\"matches\":[{\"key\":\"<group key>\",\"targetId\":<record id or null>}]} — no prose, no markdown.";

        public static string BuildUserPrompt(
            string sourceTitle,
            IReadOnlyList<string> sourceAuthors,
            IReadOnlyList<ClusterInput> clusters,
            IReadOnlyList<CandidateInput> candidates)
        {
            var sb = new StringBuilder();
            sb.Append("The groups come from the record \"").Append(sourceTitle).Append('"');
            if (sourceAuthors.Count > 0)
            {
                sb.Append(" by ").Append(string.Join(", ", sourceAuthors));
            }
            sb.AppendLine(".");

            sb.AppendLine("Library records (id: title):");
            foreach (var candidate in candidates.Take(MaxCandidates))
            {
                sb.Append(candidate.Id).Append(": ").AppendLine(candidate.Title);
            }

            sb.AppendLine("Groups:");
            foreach (var cluster in clusters)
            {
                sb.Append("- key=").Append(cluster.Key)
                  .Append(" name=\"").Append(cluster.DisplayName).Append('"');
                if (cluster.SampleFileNames.Count > 0)
                {
                    sb.Append(" files: ")
                      .Append(string.Join("; ", cluster.SampleFileNames.Take(MaxSampleFileNames)));
                }
                sb.AppendLine();
            }

            return sb.ToString();
        }

        /// <summary>
        /// Lenient parse of the model's answer: strips markdown fences and any
        /// prose around the first JSON object, validates every targetId
        /// against <paramref name="validTargetIds"/>, and drops anything else.
        /// Returns an empty map on garbage — the caller then keeps the
        /// deterministic suggestions untouched.
        /// </summary>
        public static Dictionary<string, int?> ParseResponse(string? responseText, IReadOnlySet<int> validTargetIds)
        {
            var result = new Dictionary<string, int?>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(responseText)) return result;

            var start = responseText.IndexOf('{');
            var end = responseText.LastIndexOf('}');
            if (start < 0 || end <= start) return result;

            try
            {
                using var doc = JsonDocument.Parse(responseText[start..(end + 1)]);
                if (!doc.RootElement.TryGetProperty("matches", out var matches)
                    || matches.ValueKind != JsonValueKind.Array)
                {
                    return result;
                }

                foreach (var match in matches.EnumerateArray())
                {
                    if (match.ValueKind != JsonValueKind.Object) continue;
                    if (!match.TryGetProperty("key", out var keyEl) || keyEl.ValueKind != JsonValueKind.String) continue;
                    var key = keyEl.GetString();
                    if (string.IsNullOrEmpty(key)) continue;

                    int? targetId = null;
                    if (match.TryGetProperty("targetId", out var idEl) && idEl.ValueKind == JsonValueKind.Number
                        && idEl.TryGetInt32(out var parsed))
                    {
                        // A hallucinated id must never become a suggestion.
                        if (!validTargetIds.Contains(parsed)) continue;
                        targetId = parsed;
                    }

                    result[key] = targetId;
                }
            }
            catch (JsonException)
            {
                result.Clear();
            }

            return result;
        }
    }
}
