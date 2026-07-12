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

namespace Listenarr.Application.Audiobooks
{
    /// <summary>
    /// Audio-probe mode for Split Collection: when a collection's file names
    /// carry no information (a renamer flattened them to "Title-001..NNN"),
    /// name/tag clustering yields one useless group — but the SHAPE of the
    /// files still betrays the book boundaries, and a short whisper probe of
    /// each candidate boundary file labels them.
    ///
    /// Two pure halves, both driven by the live case that shaped them (record
    /// 1023: a whole 8-book series, 158 uniformly-renamed files, 83.8h):
    ///  - <see cref="PickProbeCandidates"/> selects the few files worth
    ///    transcribing: tiny files (retail intros/epilogues ride in 1-2 MB
    ///    files), encode-rate shifts (each source rip has its own bitrate),
    ///    whole-book-length single files, and each of their successors.
    ///    158 files → ~15 probes.
    ///  - <see cref="BuildGroups"/> turns the probe transcripts into proposed
    ///    groups: a spoken publisher announcement ("Penguin Audio presents X
    ///    by Y") or a retail opener ("This is Audible") STARTS a group at that
    ///    file; a spoken "Epilogue" ENDS one after it. Labels are extracted
    ///    from announcements where possible; everything else stays with its
    ///    running group.
    ///
    /// Advisory only: the human confirms every group in the split preview UI,
    /// so boundaries err toward "only where evidence exists" — an unprobed or
    /// silent file can never open a group.
    /// </summary>
    public static class SplitAudioProbePlanner
    {
        /// <summary>Cap on probe candidates — each is a ~40s whisper run.</summary>
        public const int MaxProbeCandidates = 24;

        /// <summary>Default seconds of audio transcribed per probe.</summary>
        public const int DefaultProbeSeconds = 75;

        /// <summary>Files at or under this duration smell like intro/epilogue stubs.</summary>
        public const double SmallFileMaxSeconds = 300;

        /// <summary>Fallback small-file test when duration is unknown.</summary>
        public const long SmallFileMaxBytes = (long)(2.5 * 1024 * 1024);

        /// <summary>Single files at or beyond this length are probably whole books.</summary>
        public const double WholeBookMinSeconds = 4 * 3600;

        /// <summary>Bytes-per-second ratio beyond which two files are different encodes.</summary>
        public const double EncodeShiftRatio = 1.5;

        public sealed record FileShape(int Id, string Name, long? SizeBytes, double? DurationSeconds);

        public sealed record ProbeCandidate(int FileId, string FileName, string Reason);

        public sealed record ProbeResult(int FileId, string? Transcript);

        public sealed record ProbeGroup(
            IReadOnlyList<int> FileIds,
            string DisplayName,
            string? Label,
            string? BoundaryTranscript);

        // Spoken publisher/credit announcements that open a retail audiobook.
        // "Red by" is whisper mishearing "read by" — live case, keep it.
        private static readonly Regex AnnouncementRegex = new(
            @"\b(?:presents|audiobook|written by|narrated by|read (?:for you )?by|performed by)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Retail platform opener — starts a rip even when the title credit is
        // missing or swallowed ("This is Audible." straight into prose).
        private static readonly Regex RetailOpenerRegex = new(
            @"^\W*this is audible\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // A spoken "Epilogue" chapter heading at the START of a probed file:
        // the book it belongs to ends with this file, so the NEXT file opens a
        // new group. Anchored to the head — "epilogue" mid-transcript is prose.
        private static readonly Regex EpilogueOpenRegex = new(
            @"^\W{0,10}epilogue\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // "<Publisher> presents <Title> by <Author>" (optionally "an unabridged
        // recording/production of"). Title capture stops at the first comma or
        // sentence break so subtitles ("Charlie's Requiem, A Going Home
        // Novella, written by...") don't bloat the label.
        private static readonly Regex PresentsTitleRegex = new(
            @"presents,?\s+(?:an?\s+unabridged\s+\w+\s+of\s+)?[""“]?(?<title>[^,.!?""”]{2,80}?)[""”]?\s*(?:,|\.|\bby\b|\bwritten by\b)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Selects the files whose openings are worth transcribing, in natural
        /// order. <paramref name="orderedFiles"/> MUST already be in natural
        /// playback order.
        /// </summary>
        public static List<ProbeCandidate> PickProbeCandidates(IReadOnlyList<FileShape> orderedFiles)
        {
            var reasons = new Dictionary<int, string>(); // index -> reason (first wins)
            void Add(int index, string reason)
            {
                if (index < 0 || index >= orderedFiles.Count) return;
                reasons.TryAdd(index, reason);
            }

            Add(0, "first file");

            for (var i = 0; i < orderedFiles.Count; i++)
            {
                var f = orderedFiles[i];
                var isSmall = f.DurationSeconds is > 0 and <= SmallFileMaxSeconds
                    || (f.DurationSeconds is null or <= 0 && f.SizeBytes is > 0 and <= SmallFileMaxBytes);
                if (isSmall)
                {
                    // The stub itself (an intro announces the NEW book, an
                    // epilogue closes the OLD one) and whatever follows it.
                    Add(i, "small file (intro/epilogue stub)");
                    Add(i + 1, "follows a small file");
                }

                if (f.DurationSeconds is >= WholeBookMinSeconds)
                {
                    Add(i, "whole-book-length file");
                    Add(i + 1, "follows a whole-book-length file");
                }

                // Encode shift: compare this file's bytes/sec against the
                // previous non-small file's. Small stubs are skipped as the
                // reference — their rates are noisy.
                if (i > 0)
                {
                    var current = BytesPerSecond(f);
                    double? previous = null;
                    for (var j = i - 1; j >= 0 && previous == null; j--)
                    {
                        var candidate = orderedFiles[j];
                        var candidateSmall = candidate.DurationSeconds is > 0 and <= SmallFileMaxSeconds;
                        if (!candidateSmall) previous = BytesPerSecond(candidate);
                    }
                    if (current is > 0 && previous is > 0)
                    {
                        var ratio = current.Value / previous.Value;
                        if (ratio >= EncodeShiftRatio || ratio <= 1.0 / EncodeShiftRatio)
                        {
                            Add(i, "encoding change (different source rip)");
                        }
                    }
                }
            }

            return reasons
                .OrderBy(kv => kv.Key)
                .Take(MaxProbeCandidates)
                .Select(kv => new ProbeCandidate(
                    orderedFiles[kv.Key].Id, orderedFiles[kv.Key].Name, kv.Value))
                .ToList();
        }

        private static double? BytesPerSecond(FileShape f)
            => f.SizeBytes is > 0 && f.DurationSeconds is > 0
                ? f.SizeBytes.Value / f.DurationSeconds.Value
                : null;

        /// <summary>
        /// Assembles proposed groups from the probe transcripts. Boundaries
        /// open ONLY at files whose own transcript shows opening evidence
        /// (announcement / retail opener), or directly after a file whose
        /// transcript is a spoken "Epilogue" heading. Files between boundaries
        /// stay with the running group.
        /// </summary>
        public static List<ProbeGroup> BuildGroups(
            IReadOnlyList<FileShape> orderedFiles,
            IReadOnlyList<ProbeResult> probes)
        {
            var transcriptById = probes
                .Where(p => !string.IsNullOrWhiteSpace(p.Transcript))
                .GroupBy(p => p.FileId)
                .ToDictionary(g => g.Key, g => g.First().Transcript!.Trim());

            // index -> the transcript that justified opening a group there
            var boundaries = new Dictionary<int, string>();
            for (var i = 0; i < orderedFiles.Count; i++)
            {
                if (!transcriptById.TryGetValue(orderedFiles[i].Id, out var transcript)) continue;

                var head = Head(transcript);
                if (RetailOpenerRegex.IsMatch(head) || AnnouncementRegex.IsMatch(head))
                {
                    // Opening evidence on the file itself beats any inherited
                    // "after an epilogue" boundary.
                    boundaries[i] = transcript;
                }
                else if (EpilogueOpenRegex.IsMatch(head) && i + 1 < orderedFiles.Count)
                {
                    boundaries.TryAdd(i + 1, transcript);
                }
            }
            boundaries.TryAdd(0, transcriptById.GetValueOrDefault(orderedFiles.Count > 0 ? orderedFiles[0].Id : -1) ?? string.Empty);

            var starts = boundaries.Keys.OrderBy(i => i).ToList();
            var groups = new List<ProbeGroup>();
            for (var g = 0; g < starts.Count; g++)
            {
                var start = starts[g];
                var end = g + 1 < starts.Count ? starts[g + 1] - 1 : orderedFiles.Count - 1;
                if (end < start) continue;

                var fileIds = Enumerable.Range(start, end - start + 1)
                    .Select(i => orderedFiles[i].Id)
                    .ToList();

                // Label from the group's OWN opening transcript when it names
                // the work; the epilogue transcript that closed the previous
                // group can't name this one.
                var openingTranscript = transcriptById.GetValueOrDefault(orderedFiles[start].Id);
                var label = openingTranscript != null ? TryExtractTitle(openingTranscript) : null;
                var boundaryTranscript = openingTranscript ?? boundaries[start];

                groups.Add(new ProbeGroup(
                    fileIds,
                    label ?? $"Part {groups.Count + 1}",
                    label,
                    string.IsNullOrWhiteSpace(boundaryTranscript) ? null : Head(boundaryTranscript, 240)));
            }

            return groups;
        }

        /// <summary>
        /// Extracts the announced work title from a spoken opening, or null.
        /// Currently the "<publisher> presents <Title> ..." form — the one
        /// pattern that reliably names the work (retail "This is Audible"
        /// openers frequently jump straight into prose without a credit).
        /// </summary>
        public static string? TryExtractTitle(string transcript)
        {
            var match = PresentsTitleRegex.Match(transcript);
            if (!match.Success) return null;
            var title = match.Groups["title"].Value.Trim().Trim('"', '“', '”');
            return title.Length is >= 2 and <= 80 ? title : null;
        }

        private static string Head(string text, int chars = 160)
        {
            var trimmed = text.TrimStart();
            // Strip a stored-transcript window marker so anchored regexes see
            // the spoken opening itself.
            if (trimmed.StartsWith("[opening]", StringComparison.OrdinalIgnoreCase))
            {
                trimmed = trimmed["[opening]".Length..].TrimStart();
            }
            return trimmed.Length <= chars ? trimmed : trimmed[..chars];
        }
    }
}
