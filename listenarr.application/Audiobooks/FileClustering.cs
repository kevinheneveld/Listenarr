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
using Listenarr.Domain.Models;

namespace Listenarr.Application.Audiobooks
{
    /// <summary>
    /// Groups a record's files into per-book clusters for the "Split
    /// collection" workflow (a multi-book dump imported onto one record —
    /// live case: 773 files spanning 34 books). Subdirectory is the strongest
    /// grouping signal; root-level files cluster by their filename with track
    /// numbering and noise stripped, so "friday-01_77.mp3" … "friday-44_77.mp3"
    /// form one "friday" cluster. Pure and deterministic for unit testing.
    /// </summary>
    public static class FileClustering
    {
        public sealed record FileCluster(string Key, string DisplayName, List<AudiobookFile> Files);

        // Trailing track/part markers: "-01_77", " Part 03 of 26", "_42",
        // " - 7 - 7", "(2 of 3)", and bare parenthesized/bracketed track
        // numbers "(10)" / "[07]" (live case: "The Rolling Stones (1)" …
        // "(50)" exploded into 50 singleton groups without it).
        private static readonly Regex TrailingNumberingRegex = new(
            @"(\s*[-_ ]\s*(part|pt|cd|disc|disk|track|chapter|ch)?\s*\d+(\s*(of|/)\s*\d+)?|\s*[(\[]\d+(\s*(of|/)\s*\d+)?[)\]]|[-_ ]\d+([_-]\d+)*)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Leading list numbering: "1 The Year of the Jackpot".
        private static readonly Regex LeadingNumberRegex = new(@"^\d{1,3}[\s.\-_]+", RegexOptions.Compiled);

        public static List<FileCluster> Cluster(IEnumerable<AudiobookFile> files, string? basePath)
        {
            var groups = new Dictionary<string, FileCluster>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in files.Where(f => !string.IsNullOrWhiteSpace(f.Path)))
            {
                var relative = MakeRelative(file.Path!, basePath);
                var slash = relative.IndexOf('/');

                string key;
                string display;
                if (slash > 0)
                {
                    // Subdirectory wins: everything inside it belongs together.
                    key = "dir:" + relative[..slash];
                    display = relative[..slash];
                }
                else
                {
                    var (stem, signature) = CleanStemWithSignature(Path.GetFileNameWithoutExtension(relative));
                    // The numbering STYLE is part of the identity: a record
                    // holding two copies of one book ("Title-NN.mp3" and
                    // "Title (N).mp3") must yield two groups, or the
                    // delete-the-duplicate-copy workflow can't target one.
                    key = "stem:" + stem.ToLowerInvariant() + "|" + signature;
                    display = stem;
                }

                if (!groups.TryGetValue(key, out var cluster))
                {
                    cluster = new FileCluster(key, display, new List<AudiobookFile>());
                    groups[key] = cluster;
                }
                cluster.Files.Add(file);
            }

            MergeUnnumberedSiblings(groups);

            return groups.Values
                .OrderByDescending(c => c.Files.Count)
                .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// An unnumbered file alongside a numbered set ("Title.mp3" next to
        /// "Title (1).mp3"…) is usually that set's FIRST track — numbering
        /// often starts at the unmarked file (users have resorted to renaming
        /// it "Title (0).mp3" by hand). Merge it into the same-stem numbered
        /// group when it plausibly IS one track (duration/size comparable to
        /// the group's median); a same-stem whole-book file is many times
        /// larger and stays its own group, as does anything ambiguous
        /// (multiple same-stem numbered groups to choose from).
        /// </summary>
        private static void MergeUnnumberedSiblings(Dictionary<string, FileCluster> groups)
        {
            var markerless = groups
                .Where(kv => kv.Key.StartsWith("stem:", StringComparison.Ordinal)
                             && kv.Key.EndsWith("|", StringComparison.Ordinal)
                             && kv.Value.Files.Count == 1)
                .ToList();

            foreach (var (key, cluster) in markerless)
            {
                // The markerless key "stem:<stem>|" is itself the shared prefix
                // of every same-stem numbered group ("stem:<stem>|<signature>").
                var candidates = groups
                    .Where(kv => kv.Key != key
                                 && kv.Key.StartsWith(key, StringComparison.Ordinal)
                                 && kv.Value.Files.Count > 0)
                    .Select(kv => kv.Value)
                    .ToList();
                if (candidates.Count != 1) continue; // none, or ambiguous (two copies)

                var target = candidates[0];
                if (!LooksLikeOneTrackOf(cluster.Files[0], target.Files)) continue;

                target.Files.Insert(0, cluster.Files[0]);
                groups.Remove(key);
            }
        }

        private static bool LooksLikeOneTrackOf(AudiobookFile file, List<AudiobookFile> groupFiles)
        {
            const double MaxRatio = 3.0;

            var fileDuration = file.DurationSeconds ?? 0;
            var durations = groupFiles.Select(f => f.DurationSeconds ?? 0).Where(d => d > 0).OrderBy(d => d).ToList();
            if (fileDuration > 0 && durations.Count > 0)
            {
                return fileDuration <= durations[durations.Count / 2] * MaxRatio;
            }

            var fileSize = file.Size ?? 0;
            var sizes = groupFiles.Select(f => f.Size ?? 0).Where(s => s > 0).OrderBy(s => s).ToList();
            if (fileSize > 0 && sizes.Count > 0)
            {
                return fileSize <= sizes[sizes.Count / 2] * MaxRatio;
            }

            // No comparable signal — stay conservative, keep it separate.
            return false;
        }

        /// <summary>
        /// Strips track numbering and list prefixes so sibling files share a
        /// stem: "(heinlein_robert)-friday-01_77" → "(heinlein_robert)-friday".
        /// Applied repeatedly because markers stack ("Part 01 of 26" + "_77").
        /// </summary>
        public static string CleanStem(string fileName) => CleanStemWithSignature(fileName).Stem;

        /// <summary>
        /// As <see cref="CleanStem"/>, plus the digit-normalized template of
        /// what was stripped ("-#_#", " (#)", " part # of #") — the marker
        /// STYLE that distinguishes two copies of the same book.
        /// </summary>
        public static (string Stem, string Signature) CleanStemWithSignature(string fileName)
        {
            var stem = fileName.Trim();
            var signatureParts = new List<string>();

            var leading = LeadingNumberRegex.Match(stem);
            if (leading.Success)
            {
                signatureParts.Add(NormalizeDigits(leading.Value));
                stem = stem[leading.Length..];
            }

            string previous;
            do
            {
                previous = stem;
                var trailing = TrailingNumberingRegex.Match(stem);
                if (trailing.Success)
                {
                    // Outermost marker first so stacked markers serialize stably.
                    signatureParts.Insert(leading.Success ? 1 : 0, NormalizeDigits(trailing.Value));
                    stem = stem[..trailing.Index].Trim();
                }
            } while (stem != previous && stem.Length > 0);

            // A stem that stripped down to nothing or to bare digits carries no
            // grouping signal — keep the original name (conservative: such
            // files stay singleton clusters instead of merging on noise).
            if (stem.Length == 0 || stem.All(char.IsDigit)) return (fileName.Trim(), string.Empty);
            return (stem, string.Join("", signatureParts));
        }

        private static string NormalizeDigits(string marker) =>
            Regex.Replace(marker.Trim().ToLowerInvariant(), @"\d+", "#");

        private static string MakeRelative(string path, string? basePath)
        {
            var normalized = path.Replace('\\', '/');
            if (!string.IsNullOrWhiteSpace(basePath))
            {
                var root = basePath.Replace('\\', '/').TrimEnd('/') + "/";
                if (normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    return normalized[root.Length..];
                }
            }
            // Fall back to the file name so an unanchored path can't smuggle in
            // its whole directory chain as a "subdirectory" cluster.
            return Path.GetFileName(normalized);
        }
    }
}
