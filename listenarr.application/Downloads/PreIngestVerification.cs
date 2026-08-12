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

namespace Listenarr.Application.Downloads
{
    /// <summary>
    /// Last-line import-time check on a completed download, run after the files
    /// are on disk but before they are committed to the library. Catches what
    /// the search-time scorer can miss because it only sees release titles:
    /// music albums that slipped past the audiobook-category filter. Conservative
    /// by design — it acts only on hard ffprobe signal (track-length distribution)
    /// and fails open whenever that signal is absent.
    /// </summary>
    public static class PreIngestVerification
    {
        // A release with at least this many audio files whose median track
        // length is below the threshold is almost certainly a music album, not
        // an audiobook. Audiobook chapters run long; music tracks run ~3-5 min.
        public const int MusicMinFileCount = 10;
        public const double MusicMaxMedianSeconds = 300;

        // ...unless the batch runs this long in total. Music albums rarely
        // exceed ~2.5 hours even as doubles, but chopped-track audiobook rips
        // routinely do (live cases: a 21-hour book in 258 files rejected as
        // "music"; then a legitimate 3.2-hour Twain collection in 47 files
        // rejected under a 4-hour cutoff). A rare long box set that slips
        // through lands in the verification engine's lap, which is the right
        // failure direction — a false reject here blocks a real book forever.
        public const double MusicMaxTotalSeconds = 2.5 * 3600;

        public sealed record Result(bool Rejected, string? Reason);

        public static readonly Result Accepted = new(false, null);

        /// <summary>
        /// Inspect the extracted metadata for every audio file in a completed
        /// download. Returns a rejection (with a human-readable reason) when the
        /// batch looks like a music album. Returns <see cref="Accepted"/> otherwise.
        /// </summary>
        public static Result Inspect(IReadOnlyList<AudioMetadata> audioFiles)
        {
            if (audioFiles == null || audioFiles.Count == 0)
                return Accepted;

            return CheckMusicShape(audioFiles);
        }

        private static Result CheckMusicShape(IReadOnlyList<AudioMetadata> audioFiles)
        {
            if (audioFiles.Count < MusicMinFileCount)
                return Accepted;

            var durations = audioFiles
                .Select(m => m.Duration.TotalSeconds)
                .Where(s => s > 0)
                .OrderBy(s => s)
                .ToList();

            // No usable duration signal — fail open rather than guess.
            if (durations.Count == 0)
                return Accepted;

            var median = Median(durations);
            var totalSeconds = durations.Sum();
            if (median < MusicMaxMedianSeconds && totalSeconds < MusicMaxTotalSeconds)
            {
                return new Result(true,
                    $"looks like a music album, not an audiobook: {audioFiles.Count} audio files, " +
                    $"median track length {median:F0}s, total {totalSeconds / 3600:F1}h " +
                    $"(threshold: {MusicMinFileCount}+ files with median < {MusicMaxMedianSeconds:F0}s and total < {MusicMaxTotalSeconds / 3600:F0}h)");
            }

            return Accepted;
        }

        private static double Median(IReadOnlyList<double> sortedAscending)
        {
            var n = sortedAscending.Count;
            return n % 2 == 1
                ? sortedAscending[n / 2]
                : (sortedAscending[(n / 2) - 1] + sortedAscending[n / 2]) / 2.0;
        }
    }
}
