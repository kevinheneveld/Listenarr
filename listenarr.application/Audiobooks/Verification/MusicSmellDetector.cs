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
using Listenarr.Application.Search;

namespace Listenarr.Application.Audiobooks.Verification
{
    /// <summary>
    /// Heuristic "smells like a music album" scorer over verification artifacts
    /// already on the record (file shape, stored transcript, heard credits) —
    /// no new transcription. Feeds a human REVIEW queue whose action is the
    /// destructive not-audiobook sweep, so the weights are tuned for precision
    /// over recall: a false "music" label invites a human to delete real
    /// content. Pure and deterministic for unit testing.
    ///
    /// NOTE: a fully automatic sweep mode (no human click) is deliberately NOT
    /// implemented — revisit as an opt-in setting once the detector's precision
    /// is proven in the field.
    /// </summary>
    public static class MusicSmellDetector
    {
        /// <summary>Score at or above which a book is surfaced for review.</summary>
        public const double CandidateThreshold = 0.5;

        /// <summary>Album shape: many short tracks, none audiobook-chapter length.</summary>
        public const int AlbumMinFiles = 6;
        public const double AlbumMedianMinSeconds = 90;
        public const double AlbumMedianMaxSeconds = 420;
        public const double AlbumMaxFileSeconds = 600;

        public sealed record Result(double Score, IReadOnlyList<string> Reasons)
        {
            public bool IsCandidate => Score >= CandidateThreshold;
        }

        // Whisper renders non-speech as bracketed/parenthesized cues or bare
        // note glyphs; lyric-heavy decodes are dominated by these plus short
        // repeated lines. We only need the cue density, not lyric analysis.
        private static readonly Regex MusicCueRegex = new(
            @"\[[^\]]{0,40}music[^\]]{0,40}\]|\([^)]{0,40}music[^)]{0,40}\)|♪|\[silence\]|\[applause\]",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Performer-flavored announcement phrasing — the way music idents talk
        // about their content, as opposed to audiobook credits ("narrated by",
        // "read by"). "performed by" appears in both worlds (full-cast audio
        // dramas!), which is why this signal alone can never cross the
        // candidate threshold.
        private static readonly Regex PerformerFlavorRegex = new(
            @"\b(?:performed\s+by|album|live\s+at|remastered|feat\.?\b|featuring|hit\s+single|track\s+\d+)",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        /// <summary>
        /// Scores one book. <paramref name="fileDurationsSeconds"/> is the tracked
        /// files' durations (nulls allowed — unknown durations weaken the shape
        /// signal rather than faking it). <paramref name="transcript"/> is the
        /// stored verification transcript; <paramref name="heardTitle"/>/<paramref name="heardAuthor"/>
        /// come from the stored verdict's heard credits; <paramref name="metadataTitle"/>/<paramref name="metadataAuthor"/>
        /// are the record's own fields.
        /// </summary>
        public static Result Score(
            VerificationStatus status,
            IReadOnlyList<double?> fileDurationsSeconds,
            string? transcript,
            string? heardTitle,
            string? heardAuthor,
            string? metadataTitle,
            string? metadataAuthor)
        {
            // Only agent-writable, non-Match verdicts are ever candidates: a Match
            // is presumed right, and a human ruling (ManuallyVerified / Rejected)
            // outranks any heuristic — in both directions.
            if (status is not (VerificationStatus.AgentFlagged or VerificationStatus.AgentUnverifiable))
            {
                return new Result(0, Array.Empty<string>());
            }

            var reasons = new List<string>();
            double score = 0;

            // --- Album shape (+0.5): the strongest signal. Audiobook chapters
            // run tens of minutes; albums are many 2-7 minute tracks. The max
            // gate keeps dramatized adaptations (short scene files mixed with
            // long parts) from qualifying.
            var known = fileDurationsSeconds.Where(d => d is > 0).Select(d => d!.Value).OrderBy(d => d).ToList();
            if (known.Count >= AlbumMinFiles)
            {
                var median = known[known.Count / 2];
                var max = known[^1];
                if (median >= AlbumMedianMinSeconds && median <= AlbumMedianMaxSeconds && max < AlbumMaxFileSeconds)
                {
                    score += 0.5;
                    reasons.Add($"album shape: {known.Count} tracks, median {Math.Round(median / 60.0, 1)} min, longest {Math.Round(max / 60.0, 1)} min");
                }
            }

            // --- Music-dominated transcript (+0.25): whisper mostly heard music
            // cues, or almost nothing despite real duration.
            if (!string.IsNullOrWhiteSpace(transcript))
            {
                var cueMatches = MusicCueRegex.Matches(transcript).Count;
                var speechChars = MusicCueRegex.Replace(transcript, "").Count(c => char.IsLetter(c));
                var totalDuration = known.Sum();
                // "Near empty" means whisper heard almost NOTHING across real
                // duration — a short-but-genuine narration snippet must not
                // qualify, so the bar is deliberately far below any real
                // opening-window transcript.
                var nearEmpty = speechChars < 40 && totalDuration > 600;
                if (cueMatches >= 3 || (cueMatches >= 1 && speechChars < 200) || nearEmpty)
                {
                    score += 0.25;
                    reasons.Add(nearEmpty
                        ? "transcript nearly empty despite audio duration"
                        : $"transcript dominated by music cues ({cueMatches} cue(s))");
                }
            }

            // --- Performer-flavored credits that DON'T match the metadata (+0.25):
            // the audio announces something, in music phrasing, and what it
            // announces isn't this book.
            if (SpokenCreditsExtractor.ContainsCreditMarkers(transcript)
                && PerformerFlavorRegex.IsMatch(transcript ?? string.Empty)
                && !HeardMatchesMetadata(heardTitle, metadataTitle)
                && !HeardMatchesMetadata(heardAuthor, metadataAuthor))
            {
                score += 0.25;
                reasons.Add("performer-style credits that don't match this book");
            }

            return new Result(Math.Min(1.0, score), reasons);
        }

        /// <summary>
        /// True when a heard claim plausibly IS the metadata value (normalized
        /// containment either way) — in which case the credits corroborate the
        /// book and must not count as a music signal.
        /// </summary>
        internal static bool HeardMatchesMetadata(string? heard, string? metadata)
        {
            if (string.IsNullOrWhiteSpace(heard) || string.IsNullOrWhiteSpace(metadata)) return false;
            var h = TitleMatcher.Normalize(heard);
            var m = TitleMatcher.Normalize(metadata);
            if (h.Length == 0 || m.Length == 0) return false;
            return h.Contains(m, StringComparison.Ordinal) || m.Contains(h, StringComparison.Ordinal);
        }
    }
}
