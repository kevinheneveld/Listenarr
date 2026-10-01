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
using Listenarr.Domain.Common;

namespace Listenarr.Application.Audiobooks
{
    // Shape-aware refinement of bulk-renamed flat sequences: encode runs and
    // tag residue (see SplitFlatSequencesByShape).
    public static partial class FileClustering
    {
        // One root-level file with both of its possible identities: the
        // filename stem (always) and the embedded tag (when usable).
        private sealed record FlatFile(
            AudiobookFile File, string StemKey, string StemDisplay, string? EmbedKey, string? EmbedDisplay);

        /// <summary>Bitrate ratio beyond which two neighbors are different encodes.</summary>
        private const double EncodeShiftRatio = 1.5;

        // An encode run this short is an odd file inside a book (a jingle, a
        // re-encoded intro), not a book — unless it is long enough to be one.
        private const int MinEncodeRunFiles = 3;
        private const double WholeBookRunSeconds = 2.5 * 3600;

        // A run of same-tagged files this small, sitting among untagged
        // neighbors, is residue of the tool that chunked the audio (only the
        // first/last chunk of each source file keeps its tags) — not a book.
        private const int MinTaggedRunFiles = 3;
        private const double MinTaggedRunSeconds = 3600;

        /// <summary>
        /// Second look at root-level files that share one filename stem and
        /// numbering style — a collection bulk-renamed to "Title-001…NNN",
        /// where the name says nothing and the natural order is the playback
        /// order. Two things the name/tag pass gets wrong there (live case: a
        /// four-book series pack, 1,408 five-minute chunks):
        ///  - Different rips sit back to back. An encode change (bitrate or
        ///    sample rate) between neighbors is a boundary no tag is needed
        ///    for, so the untagged files split into one group per encode run
        ///    instead of one 1,351-file blob spanning three books.
        ///  - Tags survive only on the first/last chunk of each source file.
        ///    Clustered by tag, those few files formed non-consecutive
        ///    "books" ("Book1": files 1 and 102). A tiny tagged run among
        ///    untagged neighbors is absorbed into them, and its tag becomes
        ///    the group's name.
        /// Substantial tagged runs keep clustering by tag exactly as before,
        /// and a sequence showing neither signal is left untouched.
        /// </summary>
        private static void SplitFlatSequencesByShape(Dictionary<string, FileCluster> groups, List<FlatFile> flatFiles)
        {
            foreach (var sequence in flatFiles.GroupBy(f => f.StemKey, StringComparer.OrdinalIgnoreCase))
            {
                var byFile = new Dictionary<AudiobookFile, FlatFile>(ReferenceEqualityComparer.Instance);
                foreach (var flat in sequence) byFile[flat.File] = flat;
                var ordered = AudiobookFileOrdering.InNaturalOrder(sequence.Select(f => f.File))
                    .Select(file => byFile[file])
                    .ToList();
                if (ordered.Count < 2) continue;

                var encodeRuns = EncodeRuns(ordered);
                var anonymousRuns = new List<(List<FlatFile> Files, string? Label)>();
                var absorbedAnyTag = false;
                foreach (var encodeRun in encodeRuns)
                {
                    foreach (var run in AnonymousRuns(encodeRun))
                    {
                        absorbedAnyTag = absorbedAnyTag || run.Label != null;
                        anonymousRuns.Add(run);
                    }
                }

                // Nothing learned: at most one untagged stretch and no tag
                // residue — the name/tag pass already has it right.
                if (anonymousRuns.Count <= 1 && !absorbedAnyTag) continue;

                foreach (var file in anonymousRuns.SelectMany(r => r.Files))
                {
                    RemoveFromGroup(groups, file.StemKey, file.File);
                    if (file.EmbedKey != null) RemoveFromGroup(groups, file.EmbedKey, file.File);
                }

                for (var i = 0; i < anonymousRuns.Count; i++)
                {
                    var (runFiles, label) = anonymousRuns[i];
                    var key = sequence.Key + "#" + (i + 1);
                    groups[key] = new FileCluster(
                        key,
                        label ?? runFiles[0].StemDisplay,
                        runFiles.Select(f => f.File).ToList(),
                        Anonymous: label == null && anonymousRuns.Count > 1);
                }
            }
        }

        private static void RemoveFromGroup(Dictionary<string, FileCluster> groups, string key, AudiobookFile file)
        {
            if (!groups.TryGetValue(key, out var cluster)) return;
            cluster.Files.Remove(file);
            if (cluster.Files.Count == 0) groups.Remove(key);
        }

        /// <summary>
        /// Cuts an ordered sequence at encode changes. Runs too small to be a
        /// book fold into their neighbor, and neighbors that turn out to share
        /// an encode after folding rejoin — so one odd file never splits a book.
        /// </summary>
        private static List<List<FlatFile>> EncodeRuns(List<FlatFile> ordered)
        {
            // Each run is judged against its first measurable file — a stable
            // reference; file-to-file comparison would drift across a VBR run.
            var raw = new List<(List<FlatFile> Files, AudiobookFile? Reference)>();
            foreach (var file in ordered)
            {
                if (raw.Count > 0 && !EncodeShift(raw[^1].Reference, file.File))
                {
                    raw[^1].Files.Add(file);
                    if (raw[^1].Reference == null && BitsPerSecond(file.File) != null)
                    {
                        raw[^1] = (raw[^1].Files, file.File);
                    }
                }
                else
                {
                    raw.Add((new List<FlatFile> { file }, BitsPerSecond(file.File) != null ? file.File : null));
                }
            }

            var runs = new List<(List<FlatFile> Files, AudiobookFile? Reference)>();
            var pendingPrefix = new List<FlatFile>();
            foreach (var run in raw)
            {
                var tooSmall = run.Files.Count < MinEncodeRunFiles
                    && run.Files.Sum(f => f.File.DurationSeconds ?? 0) < WholeBookRunSeconds;
                if (tooSmall)
                {
                    if (runs.Count > 0) runs[^1].Files.AddRange(run.Files);
                    else pendingPrefix.AddRange(run.Files);
                    continue;
                }

                if (runs.Count > 0 && (run.Reference == null || !EncodeShift(runs[^1].Reference, run.Reference)))
                {
                    runs[^1].Files.AddRange(run.Files);
                    continue;
                }

                run.Files.InsertRange(0, pendingPrefix);
                pendingPrefix = new List<FlatFile>();
                runs.Add(run);
            }

            if (runs.Count == 0) return new List<List<FlatFile>> { pendingPrefix };
            return runs.Select(r => r.Files).ToList();
        }

        private static bool EncodeShift(AudiobookFile? reference, AudiobookFile next)
        {
            if (reference == null) return false;

            if (reference.SampleRate is > 0 && next.SampleRate is > 0 && reference.SampleRate != next.SampleRate)
            {
                return true;
            }

            var current = BitsPerSecond(next);
            if (current == null) return false;
            var ratio = current.Value / BitsPerSecond(reference)!.Value;
            return ratio >= EncodeShiftRatio || ratio <= 1.0 / EncodeShiftRatio;
        }

        private static double? BitsPerSecond(AudiobookFile file)
        {
            if (file.Bitrate is > 0) return file.Bitrate.Value;
            return file.Size is > 0 && file.DurationSeconds is > 0
                ? file.Size.Value * 8 / file.DurationSeconds.Value
                : null;
        }

        /// <summary>
        /// Within one encode run: the contiguous stretches of untagged files,
        /// each together with the tag residue it absorbs. A stretch is only
        /// anonymous when untagged files are its majority — a stretch of
        /// mostly single-file tagged works (a story collection with one
        /// untagged file) keeps clustering by tag.
        /// </summary>
        private static List<(List<FlatFile> Files, string? Label)> AnonymousRuns(List<FlatFile> encodeRun)
        {
            // Mark tagged runs too small to be a book.
            var residue = new bool[encodeRun.Count];
            for (var start = 0; start < encodeRun.Count;)
            {
                var end = start;
                while (end + 1 < encodeRun.Count
                    && string.Equals(encodeRun[end + 1].EmbedKey, encodeRun[start].EmbedKey, StringComparison.OrdinalIgnoreCase))
                {
                    end++;
                }

                if (encodeRun[start].EmbedKey != null)
                {
                    var count = end - start + 1;
                    var seconds = 0d;
                    for (var i = start; i <= end; i++) seconds += encodeRun[i].File.DurationSeconds ?? 0;
                    if (count < MinTaggedRunFiles && seconds < MinTaggedRunSeconds)
                    {
                        for (var i = start; i <= end; i++) residue[i] = true;
                    }
                }
                start = end + 1;
            }

            var result = new List<(List<FlatFile>, string?)>();
            for (var start = 0; start < encodeRun.Count;)
            {
                if (encodeRun[start].EmbedKey != null && !residue[start])
                {
                    start++;
                    continue;
                }

                var end = start;
                while (end + 1 < encodeRun.Count && (encodeRun[end + 1].EmbedKey == null || residue[end + 1])) end++;

                var stretch = encodeRun.GetRange(start, end - start + 1);
                var tagged = stretch.Where(f => f.EmbedKey != null).ToList();
                if (stretch.Count - tagged.Count >= Math.Max(1, tagged.Count))
                {
                    var label = tagged
                        .GroupBy(f => f.EmbedKey!, StringComparer.OrdinalIgnoreCase)
                        .OrderByDescending(g => g.Count())
                        .Select(g => g.First().EmbedDisplay)
                        .FirstOrDefault();
                    result.Add((stretch, label));
                }
                start = end + 1;
            }
            return result;
        }
    }
}
