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

        /// <summary>
        /// …but only when also well short of the collection's typical file:
        /// in a pack chopped into uniform five-minute chunks every file sits
        /// at the absolute limit, and "small" has to mean small for THIS
        /// collection (live case: 1,408 ~300s chunks — the absolute test
        /// flagged files 34-55 and spent the whole probe budget before
        /// reaching the first real boundary).
        /// </summary>
        public const double SmallFileTypicalRatio = 0.75;

        /// <summary>Durations within this fraction of each other are the same chunk length.</summary>
        public const double UniformChunkTolerance = 0.01;

        /// <summary>Chunk lengths differing by more than this are different source files.</summary>
        public const double ChunkLengthShiftRatio = 0.03;

        /// <summary>Neighbors that must agree before a stretch counts as uniformly chunked.</summary>
        private const int UniformRunLength = 3;

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
        // Quoted form first: whisper sometimes quotes the title and then keeps
        // going without punctuation ('presents "The Game of Thrones" Book 1 of
        // "A Song of Ice and Fire" by…'), which the delimiter-based pattern
        // below cannot close.
        private static readonly Regex QuotedPresentsTitleRegex = new(
            @"presents,?\s+(?:an?\s+unabridged\s+\w+\s+of\s+)?[""“](?<title>[^""”]{2,80})[""”]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Where an announced title stops being the work's own name and starts
        // placing it in a series ("A Game of Thrones | book one of A Song of
        // Ice and Fire").
        private static readonly Regex SeriesMarkerRegex = new(
            @"\b(?:book|volume|vol|part)\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

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
            // index -> (reason, structural). First reason wins, except that a
            // structural one replaces a stub-shaped one: structural suspects
            // (encode/chunk seams, whole books) mark source-file boundaries,
            // where a stub is only a guess from size — so they outrank stubs
            // when the cap bites.
            var reasons = new Dictionary<int, (string Reason, bool Structural)>();
            void Add(int index, string reason, bool structural)
            {
                if (index < 0 || index >= orderedFiles.Count) return;
                if (!reasons.TryGetValue(index, out var existing) || (structural && !existing.Structural))
                {
                    reasons[index] = (reason, structural);
                }
            }

            var smallMaxSeconds = SmallMaxSeconds(orderedFiles);

            Add(0, "first file", true);

            for (var i = 0; i < orderedFiles.Count; i++)
            {
                var f = orderedFiles[i];
                var isSmall = f.DurationSeconds is > 0 && f.DurationSeconds <= smallMaxSeconds
                    || (f.DurationSeconds is null or <= 0 && f.SizeBytes is > 0 and <= SmallFileMaxBytes);
                var isChunkTail = f.DurationSeconds is > 0
                    && IsUniform(orderedFiles, i - UniformRunLength, i - 1)
                    && f.DurationSeconds < orderedFiles[i - 1].DurationSeconds * (1 - ChunkLengthShiftRatio)
                    && !IsUniform(orderedFiles, i, i + UniformRunLength - 1);
                if (isChunkTail)
                {
                    // The short last chunk of a source file that was chopped
                    // into equal pieces: it is the END of that file, so only
                    // what follows can open a book.
                    Add(i + 1, "follows the short tail of a chunked source file", true);
                }
                else if (isSmall)
                {
                    // The stub itself (an intro announces the NEW book, an
                    // epilogue closes the OLD one) and whatever follows it.
                    Add(i, "small file (intro/epilogue stub)", false);
                    Add(i + 1, "follows a small file", false);
                }

                if (f.DurationSeconds is >= WholeBookMinSeconds)
                {
                    Add(i, "whole-book-length file", true);
                    Add(i + 1, "follows a whole-book-length file", true);
                }

                // Chunk-length change: a new run of equal-length chunks whose
                // length differs from the run before it is a different source
                // file — the one seam a full-length last chunk leaves behind
                // (no short tail, same encode).
                if (IsUniform(orderedFiles, i, i + UniformRunLength - 1))
                {
                    // The run before either ends right here, or one file
                    // back with a short tail in between — but a predecessor
                    // already at the new length is this run's own first file,
                    // not a tail to look past.
                    var before = IsUniform(orderedFiles, i - UniformRunLength, i - 1)
                        ? i - UniformRunLength
                        : !IsUniform(orderedFiles, i - 1, i)
                          && IsUniform(orderedFiles, i - UniformRunLength - 1, i - 2) ? i - UniformRunLength - 1 : -1;
                    if (before >= 0)
                    {
                        var ratio = f.DurationSeconds!.Value / orderedFiles[before].DurationSeconds!.Value;
                        if (Math.Abs(ratio - 1) > ChunkLengthShiftRatio)
                        {
                            Add(i, "chunk length change (new source file)", true);
                        }
                    }
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
                        var candidateSmall = candidate.DurationSeconds is > 0 && candidate.DurationSeconds <= smallMaxSeconds;
                        if (!candidateSmall) previous = BytesPerSecond(candidate);
                    }
                    if (current is > 0 && previous is > 0)
                    {
                        var ratio = current.Value / previous.Value;
                        if (ratio >= EncodeShiftRatio || ratio <= 1.0 / EncodeShiftRatio)
                        {
                            Add(i, "encoding change (different source rip)", true);
                        }
                    }
                }
            }

            return reasons
                .OrderBy(kv => kv.Value.Structural ? 0 : 1)
                .ThenBy(kv => kv.Key)
                .Take(MaxProbeCandidates)
                .OrderBy(kv => kv.Key)
                .Select(kv => new ProbeCandidate(
                    orderedFiles[kv.Key].Id, orderedFiles[kv.Key].Name, kv.Value.Reason))
                .ToList();
        }

        /// <summary>
        /// The duration at or under which a file counts as a stub: the
        /// absolute limit, lowered for collections whose typical file is
        /// itself that short.
        /// </summary>
        private static double SmallMaxSeconds(IReadOnlyList<FileShape> files)
        {
            var durations = files
                .Select(f => f.DurationSeconds ?? 0)
                .Where(d => d > 0)
                .OrderBy(d => d)
                .ToList();
            if (durations.Count == 0) return SmallFileMaxSeconds;
            return Math.Min(SmallFileMaxSeconds, durations[durations.Count / 2] * SmallFileTypicalRatio);
        }

        /// <summary>True when files [from..to] all have known durations within the chunk tolerance of the first.</summary>
        private static bool IsUniform(IReadOnlyList<FileShape> files, int from, int to)
        {
            if (from < 0 || to >= files.Count || to <= from) return false;
            var reference = files[from].DurationSeconds;
            if (reference is null or <= 0) return false;
            for (var i = from + 1; i <= to; i++)
            {
                var d = files[i].DurationSeconds;
                if (d is null or <= 0 || Math.Abs(d.Value / reference.Value - 1) > UniformChunkTolerance) return false;
            }
            return true;
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

            // A whole-book-length file IS a complete book no matter what its
            // opening says — rips without retail announcements must not run
            // together (live case: a 17-file collection of 4-7.6h m4bs merged
            // into a handful of arbitrary groups because none of the openings
            // announced anything).
            for (var i = 0; i < orderedFiles.Count; i++)
            {
                if (orderedFiles[i].DurationSeconds is >= WholeBookMinSeconds)
                {
                    boundaries.TryAdd(i, transcriptById.GetValueOrDefault(orderedFiles[i].Id) ?? string.Empty);
                }
            }
            boundaries.TryAdd(0, transcriptById.GetValueOrDefault(orderedFiles.Count > 0 ? orderedFiles[0].Id : -1) ?? string.Empty);

            // A multi-part retail book re-announces its title at the top of
            // every part ("…presents A Game of Thrones, Book One…" four times
            // over). A boundary that only repeats the running group's title
            // is the same book continuing, not a new one.
            var starts = new List<int>();
            string? runningTitle = null;
            foreach (var index in boundaries.Keys.OrderBy(i => i))
            {
                var announced = transcriptById.TryGetValue(orderedFiles[index].Id, out var heard)
                    ? TryExtractTitle(heard)
                    : null;
                if (starts.Count > 0 && announced != null && runningTitle != null && SameWork(announced, runningTitle))
                {
                    continue;
                }
                starts.Add(index);
                runningTitle = announced;
            }

            // File names only label a group when they differ across the
            // collection. Bulk-renamed to one stem ("Title-001…NNN"), every
            // name is the SOURCE record's title — suggesting a destination
            // from it would send each unannounced group to whichever other
            // record shares that title.
            var namesCarrySignal = orderedFiles
                .Select(f => FileClustering.CleanStem(FileNameStem(f.Name) ?? string.Empty))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Skip(1)
                .Any();

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
                // No announced title: the opening file's own name is a far
                // better label than "Part N" — it usually names the book, so
                // the destination suggester can work with it.
                var openingName = FileNameStem(orderedFiles[start].Name);
                if (namesCarrySignal) label ??= openingName;
                var boundaryTranscript = openingTranscript ?? boundaries[start];

                groups.Add(new ProbeGroup(
                    fileIds,
                    label ?? openingName ?? $"Part {groups.Count + 1}",
                    label,
                    string.IsNullOrWhiteSpace(boundaryTranscript) ? null : Head(boundaryTranscript, 240)));
            }

            return groups;
        }

        private static string? FileNameStem(string? name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            var stem = Path.GetFileNameWithoutExtension(name.Trim());
            return string.IsNullOrWhiteSpace(stem) ? null : stem;
        }

        /// <summary>
        /// Extracts the announced work title from a spoken opening, or null.
        /// Currently the "<publisher> presents <Title> ..." form — the one
        /// pattern that reliably names the work (retail "This is Audible"
        /// openers frequently jump straight into prose without a credit).
        /// </summary>
        public static string? TryExtractTitle(string transcript)
        {
            var match = QuotedPresentsTitleRegex.Match(transcript);
            if (!match.Success) match = PresentsTitleRegex.Match(transcript);
            if (!match.Success) return null;
            var title = match.Groups["title"].Value.Trim().Trim('"', '“', '”');
            return title.Length is >= 2 and <= 80 ? title : null;
        }

        /// <summary>
        /// True when two announced titles name the same work: equal once
        /// leading articles and any series placement ("…book one of A Song of
        /// Ice and Fire") are set aside — whisper hears "A Game of Thrones"
        /// and "The Game of Thrones" for the same book, and only sometimes
        /// punctuates before "Book One". Sibling titles that merely share a
        /// prefix ("Dune" / "Dune Messiah") stay distinct.
        /// </summary>
        public static bool SameWork(string announcedA, string announcedB)
        {
            var a = WorkName(announcedA);
            var b = WorkName(announcedB);
            return a.Length > 0 && string.Equals(a, b, StringComparison.Ordinal);
        }

        private static string WorkName(string announced)
        {
            var marker = SeriesMarkerRegex.Match(announced);
            var own = marker.Success && marker.Index > 0 ? announced[..marker.Index] : announced;
            var tokens = TitleMatcher.Normalize(own)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .SkipWhile(t => t is "a" or "an" or "the");
            return string.Join(' ', tokens);
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
