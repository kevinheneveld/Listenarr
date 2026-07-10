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
    /// Prompt building and response parsing for the AI library sweep: a
    /// language model reviews records' tracked file names against their
    /// metadata and flags ones whose files look like something else entirely
    /// (a music album, a different book, a whole collection under one title).
    /// Complements whisper verification — that listens to the audio and takes
    /// ~5 minutes a book; this reads the file names and vets dozens per model
    /// call, catching what names alone give away.
    ///
    /// Flag-only by design, mirroring the music sweep's deliberate no-auto
    /// mode: verdicts land in front of a human, nothing is deleted or moved.
    /// </summary>
    public static class AiLibrarySweepJudge
    {
        public const int RecordsPerCall = 10;
        public const int MaxFileNamesPerRecord = 6;

        public sealed record RecordInput(int Id, string Title, IReadOnlyList<string> Authors, int FileCount, IReadOnlyList<string> SampleFileNames);
        public sealed record SweepVerdict(int Id, string Reason);

        public static string BuildSystemPrompt() =>
            "You audit an audiobook library. For each record you get the book's title, author, " +
            "total file count, and sample file names from its folder. Flag records whose files are " +
            "clearly NOT that audiobook: music albums or discographies, a different book, video, " +
            "or a multi-book collection filed under a single book's title. " +
            "Track numbering, chapter naming, and abbreviations of the book's own title are normal — do not flag them. " +
            "Only flag when the mismatch is obvious; when unsure, stay silent about that record. " +
            "Respond with ONLY a JSON object of the form " +
            "{\"suspicious\":[{\"id\":<record id>,\"reason\":\"<short reason>\"}]} — an empty list when everything looks right. No prose, no markdown.";

        public static string BuildUserPrompt(IReadOnlyList<RecordInput> records)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Records:");
            foreach (var record in records)
            {
                sb.Append("- id=").Append(record.Id)
                  .Append(" title=\"").Append(record.Title).Append('"');
                if (record.Authors.Count > 0)
                {
                    sb.Append(" author=\"").Append(string.Join(", ", record.Authors)).Append('"');
                }
                sb.Append(" files=").Append(record.FileCount);
                if (record.SampleFileNames.Count > 0)
                {
                    sb.Append(" samples: ")
                      .Append(string.Join("; ", record.SampleFileNames.Take(MaxFileNamesPerRecord)));
                }
                sb.AppendLine();
            }
            return sb.ToString();
        }

        /// <summary>
        /// Lenient parse (fences/prose tolerated); ids not in
        /// <paramref name="validIds"/> are dropped as hallucinations and
        /// structural garbage yields an empty list — nothing gets flagged on
        /// a bad answer.
        /// </summary>
        public static List<SweepVerdict> ParseResponse(string? responseText, IReadOnlySet<int> validIds)
        {
            var verdicts = new List<SweepVerdict>();
            if (string.IsNullOrWhiteSpace(responseText)) return verdicts;

            var start = responseText.IndexOf('{');
            var end = responseText.LastIndexOf('}');
            if (start < 0 || end <= start) return verdicts;

            try
            {
                using var doc = JsonDocument.Parse(responseText[start..(end + 1)]);
                if (!doc.RootElement.TryGetProperty("suspicious", out var suspicious)
                    || suspicious.ValueKind != JsonValueKind.Array)
                {
                    return verdicts;
                }

                foreach (var item in suspicious.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    if (!item.TryGetProperty("id", out var idEl)
                        || idEl.ValueKind != JsonValueKind.Number
                        || !idEl.TryGetInt32(out var id)
                        || !validIds.Contains(id))
                    {
                        continue;
                    }

                    var reason = item.TryGetProperty("reason", out var reasonEl) && reasonEl.ValueKind == JsonValueKind.String
                        ? reasonEl.GetString() ?? "flagged"
                        : "flagged";
                    verdicts.Add(new SweepVerdict(id, reason));
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
