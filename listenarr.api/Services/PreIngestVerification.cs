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
using Listenarr.Api.Services.Scoring;
using Listenarr.Domain.Models;

namespace Listenarr.Api.Services
{
    /// <summary>
    /// Last-line import-time check on a completed download, run after the files
    /// are on disk but before they are committed to the library. Catches two
    /// things the search-time scorer can miss because it only sees release
    /// titles: music albums that slipped past the audiobook-category filter,
    /// and editions in a language the profile does not want. Conservative by
    /// design — it acts only on hard ffprobe signal (track-length distribution,
    /// explicit language tags) and fails open whenever that signal is absent.
    /// </summary>
    public static class PreIngestVerification
    {
        // A release with at least this many audio files whose median track
        // length is below the threshold is almost certainly a music album, not
        // an audiobook. Audiobook chapters run long; music tracks run ~3-5 min.
        public const int MusicMinFileCount = 10;
        public const double MusicMaxMedianSeconds = 300;

        public sealed record Result(bool Rejected, string? Reason);

        public static readonly Result Accepted = new(false, null);

        /// <summary>
        /// Inspect the extracted metadata for every audio file in a completed
        /// download. Returns a rejection (with a human-readable reason) when the
        /// batch looks like a music album, or when an audio file carries an
        /// explicit language tag that is not among the profile's preferred
        /// languages. Returns <see cref="Accepted"/> otherwise.
        /// </summary>
        public static Result Inspect(
            IReadOnlyList<AudioMetadata> audioFiles,
            IEnumerable<string>? preferredLanguages)
        {
            if (audioFiles == null || audioFiles.Count == 0)
                return Accepted;

            var musicResult = CheckMusicShape(audioFiles);
            if (musicResult.Rejected)
                return musicResult;

            return CheckLanguage(audioFiles, preferredLanguages);
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
            if (median < MusicMaxMedianSeconds)
            {
                return new Result(true,
                    $"looks like a music album, not an audiobook: {audioFiles.Count} audio files, " +
                    $"median track length {median:F0}s (threshold: {MusicMinFileCount}+ files with median < {MusicMaxMedianSeconds:F0}s)");
            }

            return Accepted;
        }

        private static Result CheckLanguage(
            IReadOnlyList<AudioMetadata> audioFiles,
            IEnumerable<string>? preferredLanguages)
        {
            var prefs = preferredLanguages?
                .Where(l => !string.IsNullOrWhiteSpace(l))
                .Select(l => l.Trim())
                .ToList();
            if (prefs == null || prefs.Count == 0)
                return Accepted; // no language preference configured — don't filter

            foreach (var file in audioFiles)
            {
                // Most audiobook rips carry no language tag at all — fail open.
                var mapped = LanguageFilter.MapLanguageTag(file.Language);
                if (mapped == null)
                    continue;

                if (!prefs.Any(p => p.Equals(mapped, StringComparison.OrdinalIgnoreCase)))
                {
                    return new Result(true,
                        $"audio language tag '{file.Language}' ({mapped}) is not among the profile's " +
                        $"preferred languages: {string.Join("/", prefs)}");
                }
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
