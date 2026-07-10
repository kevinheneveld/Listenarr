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
        public sealed record SweepVerdict(int Id, string Reason, string Evidence);

        // First live run flagged 7/7 records falsely — the model invented
        // criteria (file extensions, file counts, title vibes) instead of
        // comparing file names against the record. Hence the explicit
        // non-reasons list, the "empty is the expected answer" framing, and
        // the required evidence quote (validated server-side against the
        // actual file names, so a fabricated claim can't survive parsing).
        public static string BuildSystemPrompt() =>
            "You audit an audiobook library. For each record you get the book's title, author, " +
            "and sample file names from its folder. Flag a record ONLY when its file names clearly " +
            "belong to a DIFFERENT work than the record: song titles from a music album, a different " +
            "book's title, video releases, or several different books' titles under one record. " +
            "The following are NEVER reasons to flag: file extension or format (.mp3, .m4b, .m4a, .flac are all normal), " +
            "how many files there are (one file or hundreds are both normal), track/part numbering, " +
            "chapter naming, '(Unabridged)' or '[Dramatized Adaptation]' tags, author names in file names, " +
            "radio dramas, or anything about the record's own title — judge only whether the FILE NAMES " +
            "match the record they are filed under. " +
            "Most records are correctly filed: an empty list is the expected answer for a normal batch. " +
            "Every flag must quote, in \"evidence\", one of the provided file names exactly as given — " +
            "the file name that shows the wrong work. " +
            "Respond with ONLY a JSON object of the form " +
            "{\"suspicious\":[{\"id\":<record id>,\"evidence\":\"<exact file name from the list>\",\"reason\":\"<what work the files actually appear to be>\"}]} " +
            "— no prose, no markdown.";

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
        /// Lenient parse (fences/prose tolerated) with two hallucination
        /// gates: ids must be in <paramref name="fileNamesById"/>, and the
        /// quoted evidence must be one of THAT record's actual submitted file
        /// names — a flag whose "proof" doesn't exist is fabricated and gets
        /// dropped (live case: "file format is not .mp3" about a file that
        /// was literally Dragon Tear.mp3). Structural garbage yields an empty
        /// list; nothing gets flagged on a bad answer.
        /// </summary>
        public static List<SweepVerdict> ParseResponse(
            string? responseText,
            IReadOnlyDictionary<int, IReadOnlyList<string>> fileNamesById)
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
                        || !fileNamesById.TryGetValue(id, out var fileNames))
                    {
                        continue;
                    }

                    if (!item.TryGetProperty("evidence", out var evidenceEl)
                        || evidenceEl.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }
                    var evidence = (evidenceEl.GetString() ?? string.Empty).Trim();
                    if (evidence.Length == 0
                        || !fileNames.Any(n => string.Equals(n, evidence, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var reason = item.TryGetProperty("reason", out var reasonEl) && reasonEl.ValueKind == JsonValueKind.String
                        ? reasonEl.GetString() ?? "flagged"
                        : "flagged";
                    verdicts.Add(new SweepVerdict(id, reason, evidence));
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
