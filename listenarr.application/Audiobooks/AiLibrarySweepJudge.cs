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
        // Enough of the whisper opening to carry the spoken credits ("X presents
        // <Title> by <Author>, narrated by…") without prose bloating the prompt.
        public const int MaxTranscriptChars = 400;

        public sealed record RecordInput(
            int Id,
            string Title,
            IReadOnlyList<string> Authors,
            int FileCount,
            IReadOnlyList<string> SampleFileNames,
            string? TranscriptExcerpt = null);

        /// <summary>What a flag's evidence quote is validated against.</summary>
        public sealed record RecordEvidence(IReadOnlyList<string> FileNames, string? TranscriptExcerpt);

        public sealed record SweepVerdict(int Id, string Reason, string Evidence);

        // First live run flagged 7/7 records falsely — the model invented
        // criteria (file extensions, file counts, title vibes) instead of
        // comparing file names against the record. Hence the explicit
        // non-reasons list, the "empty is the expected answer" framing, and
        // the required evidence quote (validated server-side against the
        // actual file names, so a fabricated claim can't survive parsing).
        public static string BuildSystemPrompt() =>
            "You audit an audiobook library. For each record you get the book's title, author, " +
            "sample file names from its folder, and — when available — 'audio-opening', a transcript " +
            "of what the audio itself says at the start. Flag a record ONLY when the evidence clearly " +
            "shows a DIFFERENT work than the record: song titles from a music album, a different " +
            "book's title, video releases, or several different books' titles under one record. " +
            "The audio-opening is the strongest signal: spoken credits naming a DIFFERENT title or " +
            "author than the record's is wrong content; spoken credits matching the record clears it. " +
            "An audio-opening that is MUSIC instead of narration — sung song lyrics, ♪ marks, or " +
            "annotations like (upbeat music) with essentially no spoken prose — means the file is a " +
            "music track, not an audiobook: flag it and quote a lyric line or the music annotation as " +
            "evidence. (A little intro music or a music-scored radio drama followed by real spoken " +
            "prose is normal — only flag when the opening is essentially ALL music or lyrics.) " +
            "The following are NEVER reasons to flag: file extension or format (.mp3, .m4b, .m4a, .flac are all normal), " +
            "how many files there are (one file or hundreds are both normal), track/part numbering, " +
            "chapter naming, '(Unabridged)' or '[Dramatized Adaptation]' tags, author names in file names, " +
            "radio dramas, an audio-opening of ordinary spoken prose with no credits (cold-open narration " +
            "is normal — but sung lyrics or ♪ marks are NOT a cold open), " +
            "or anything about the record's own title. " +
            "Most records are correctly filed: an empty list is the expected answer for a normal batch. " +
            "Every flag must quote, in \"evidence\", either one of the provided file names exactly as " +
            "given, or the exact fragment of the audio-opening that names the wrong work. " +
            "Respond with ONLY a JSON object of the form " +
            "{\"suspicious\":[{\"id\":<record id>,\"evidence\":\"<exact quote>\",\"reason\":\"<what work the audio/files actually appear to be>\"}]} " +
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
                if (!string.IsNullOrWhiteSpace(record.TranscriptExcerpt))
                {
                    sb.AppendLine();
                    sb.Append("  audio-opening: \"")
                      .Append(Truncate(record.TranscriptExcerpt!, MaxTranscriptChars))
                      .Append('"');
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
            IReadOnlyDictionary<int, RecordEvidence> evidenceById)
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
                        || !evidenceById.TryGetValue(id, out var recordEvidence))
                    {
                        continue;
                    }

                    if (!item.TryGetProperty("evidence", out var evidenceEl)
                        || evidenceEl.ValueKind != JsonValueKind.String)
                    {
                        continue;
                    }
                    var evidence = (evidenceEl.GetString() ?? string.Empty).Trim();
                    var evidenceIsFileName = recordEvidence.FileNames
                        .Any(n => string.Equals(n, evidence, StringComparison.OrdinalIgnoreCase));
                    // Transcript quotes need only be a substring — the model
                    // legitimately excerpts the credit phrase, not the whole
                    // opening. Require some substance so a bare "the" can't
                    // pass as proof — except ♪, which no amount of length
                    // padding makes more probative: a note glyph only exists
                    // in a transcript because whisper heard singing.
                    var evidenceIsTranscriptQuote = (evidence.Length >= 12 || evidence.Contains('♪'))
                        && !string.IsNullOrWhiteSpace(recordEvidence.TranscriptExcerpt)
                        && recordEvidence.TranscriptExcerpt!.Contains(evidence, StringComparison.OrdinalIgnoreCase);
                    if (evidence.Length == 0 || (!evidenceIsFileName && !evidenceIsTranscriptQuote))
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

        /// <summary>
        /// Truncation shared by prompt building and the workflow, so the
        /// transcript the evidence gate validates against is the same one the
        /// model actually saw.
        /// </summary>
        public static string Truncate(string text, int maxChars)
        {
            var trimmed = text.Trim();
            return trimmed.Length <= maxChars ? trimmed : trimmed[..maxChars];
        }
    }
}
