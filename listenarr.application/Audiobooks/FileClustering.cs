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

        // Trailing track/part markers: "-01_77", " Part 03 of 26", "_42", " - 7 - 7", "(2 of 3)".
        private static readonly Regex TrailingNumberingRegex = new(
            @"(\s*[-_ ]\s*(part|pt|cd|disc|disk|track|chapter|ch)?\s*\d+(\s*(of|/)\s*\d+)?|\s*\(\d+\s*(of|/)\s*\d+\)|[-_ ]\d+([_-]\d+)*)\s*$",
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
                    var stem = CleanStem(Path.GetFileNameWithoutExtension(relative));
                    key = "stem:" + stem.ToLowerInvariant();
                    display = stem;
                }

                if (!groups.TryGetValue(key, out var cluster))
                {
                    cluster = new FileCluster(key, display, new List<AudiobookFile>());
                    groups[key] = cluster;
                }
                cluster.Files.Add(file);
            }

            return groups.Values
                .OrderByDescending(c => c.Files.Count)
                .ThenBy(c => c.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        /// <summary>
        /// Strips track numbering and list prefixes so sibling files share a
        /// stem: "(heinlein_robert)-friday-01_77" → "(heinlein_robert)-friday".
        /// Applied repeatedly because markers stack ("Part 01 of 26" + "_77").
        /// </summary>
        public static string CleanStem(string fileName)
        {
            var stem = fileName.Trim();
            stem = LeadingNumberRegex.Replace(stem, string.Empty);
            string previous;
            do
            {
                previous = stem;
                stem = TrailingNumberingRegex.Replace(stem, string.Empty).Trim();
            } while (stem != previous && stem.Length > 0);

            // A stem that stripped down to nothing or to bare digits carries no
            // grouping signal — keep the original name (conservative: such
            // files stay singleton clusters instead of merging on noise).
            if (stem.Length == 0 || stem.All(char.IsDigit)) return fileName.Trim();
            return stem;
        }

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
