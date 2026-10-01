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

namespace Listenarr.Application.Audiobooks
{
    /// <summary>
    /// Answers "is this copy better than the one the record already has?" for
    /// the Split Collection workflow, where a group of files is about to land
    /// on a record that is not empty (live case: a series pack carried a
    /// 128 kbps copy of a book the library held at 64 kbps). Completeness
    /// against the catalog runtime is judged first — a complete copy beats a
    /// partial one at any bitrate — then effective bitrate (bytes per second
    /// of audio), the same yardstick the duplicate-copy keeper uses. Pure and
    /// advisory: the human confirms every replacement.
    /// </summary>
    public static class CopyQualityComparer
    {
        public const string VerdictEmpty = "empty";
        public const string VerdictIdentical = "identical";
        public const string VerdictBetter = "better";
        public const string VerdictWorse = "worse";
        public const string VerdictSimilar = "similar";
        public const string VerdictDifferent = "different";
        public const string VerdictUnknown = "unknown";

        // A copy within this window of the catalog runtime holds the whole
        // book; rips drift by padding and credits, not by tenths.
        private const double CompleteMinRatio = 0.9;
        private const double CompleteMaxRatio = 1.15;

        // Without a catalog runtime, two copies whose lengths disagree by
        // more than this are not the same audio (abridged vs unabridged, or
        // one of them partial) and their bitrates are not comparable.
        private const double SameAudioMinRatio = 0.9;

        // Bitrates this close are a wash — not worth replacing files over.
        private const double BetterBitrateRatio = 1.2;

        /// <summary>The measurable shape of one copy of a book.</summary>
        public sealed record CopySummary(
            int FileCount,
            long TotalBytes,
            double TotalDurationSeconds,
            bool DurationsComplete,
            int? BitrateKbps,
            string? Codec);

        public sealed record Comparison(string Verdict, string Reason);

        public static CopySummary Summarize(IReadOnlyCollection<AudiobookFile> files)
        {
            var totalBytes = files.Sum(f => f.Size ?? 0);
            var totalSeconds = files.Sum(f => f.DurationSeconds ?? 0);
            var durationsComplete = files.Count > 0 && files.All(f => f.DurationSeconds is > 0);
            int? kbps = durationsComplete && totalBytes > 0
                ? (int)Math.Round(totalBytes * 8 / totalSeconds / 1000)
                : null;

            // The codec carrying the most audio speaks for the copy.
            var codec = files
                .Where(f => !string.IsNullOrWhiteSpace(f.Codec))
                .GroupBy(f => f.Codec!.Trim().ToLowerInvariant())
                .OrderByDescending(g => g.Sum(f => f.DurationSeconds ?? 0))
                .ThenByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault();

            return new CopySummary(files.Count, totalBytes, totalSeconds, durationsComplete, kbps, codec);
        }

        /// <summary>True when both copies are the same multiset of file sizes — the same download twice.</summary>
        public static bool SizesIdentical(IReadOnlyCollection<AudiobookFile> a, IReadOnlyCollection<AudiobookFile> b)
        {
            if (a.Count == 0 || a.Count != b.Count) return false;
            if (a.Any(f => f.Size is null or <= 0) || b.Any(f => f.Size is null or <= 0)) return false;
            return a.Select(f => f.Size!.Value).OrderBy(s => s)
                .SequenceEqual(b.Select(f => f.Size!.Value).OrderBy(s => s));
        }

        public static Comparison Compare(
            CopySummary incoming,
            CopySummary existing,
            double? expectedRuntimeSeconds,
            bool sizesIdentical)
        {
            if (existing.FileCount == 0)
            {
                return new Comparison(VerdictEmpty, "The record has no files yet.");
            }

            if (sizesIdentical)
            {
                return new Comparison(
                    VerdictIdentical,
                    "Every file matches the existing copy size for size — this is the same download again.");
            }

            if (!incoming.DurationsComplete || !existing.DurationsComplete)
            {
                return new Comparison(
                    VerdictUnknown,
                    "Some files have no duration on record, so the two copies cannot be compared.");
            }

            if (expectedRuntimeSeconds is > 0)
            {
                var expected = expectedRuntimeSeconds.Value;
                if (incoming.TotalDurationSeconds > expected * CompleteMaxRatio)
                {
                    return new Comparison(
                        VerdictDifferent,
                        $"This group runs {Hours(incoming.TotalDurationSeconds)} but the book is {Hours(expected)} — it holds more than this one book.");
                }

                var incomingComplete = incoming.TotalDurationSeconds >= expected * CompleteMinRatio;
                var existingComplete = existing.TotalDurationSeconds >= expected * CompleteMinRatio
                    && existing.TotalDurationSeconds <= expected * CompleteMaxRatio;
                if (incomingComplete && !existingComplete)
                {
                    return new Comparison(
                        VerdictBetter,
                        $"This copy is the whole book ({Hours(incoming.TotalDurationSeconds)} of {Hours(expected)}); the existing one runs {Hours(existing.TotalDurationSeconds)}.");
                }
                if (!incomingComplete && existingComplete)
                {
                    return new Comparison(
                        VerdictWorse,
                        $"This copy is incomplete ({Hours(incoming.TotalDurationSeconds)} of {Hours(expected)}); the existing one is the whole book.");
                }
                if (!incomingComplete)
                {
                    return CompareLengths(incoming, existing) ?? CompareBitrates(incoming, existing);
                }

                return CompareBitrates(incoming, existing);
            }

            return CompareLengths(incoming, existing) ?? CompareBitrates(incoming, existing);
        }

        private static Comparison? CompareLengths(CopySummary incoming, CopySummary existing)
        {
            var ratio = Math.Min(incoming.TotalDurationSeconds, existing.TotalDurationSeconds)
                        / Math.Max(incoming.TotalDurationSeconds, existing.TotalDurationSeconds);
            return ratio >= SameAudioMinRatio
                ? null
                : new Comparison(
                    VerdictDifferent,
                    $"The lengths disagree ({Hours(incoming.TotalDurationSeconds)} vs the existing {Hours(existing.TotalDurationSeconds)}) — a different edition, or one of them is incomplete.");
        }

        private static Comparison CompareBitrates(CopySummary incoming, CopySummary existing)
        {
            if (incoming.BitrateKbps is not > 0 || existing.BitrateKbps is not > 0)
            {
                return new Comparison(VerdictUnknown, "Bitrate is not on record for both copies.");
            }

            var mine = incoming.BitrateKbps.Value;
            var theirs = existing.BitrateKbps.Value;
            // Bitrate only ranks copies fairly within one codec; across codecs
            // it is still the best number on hand, so say what it is.
            var codecNote = incoming.Codec != null && existing.Codec != null
                && !string.Equals(incoming.Codec, existing.Codec, StringComparison.OrdinalIgnoreCase)
                ? $" Codecs differ ({incoming.Codec} vs {existing.Codec}), so bitrate is only a rough guide."
                : string.Empty;

            var ratio = (double)mine / theirs;
            if (ratio >= BetterBitrateRatio)
            {
                return new Comparison(VerdictBetter, $"{mine} kbps against the existing {theirs} kbps.{codecNote}");
            }
            if (ratio <= 1.0 / BetterBitrateRatio)
            {
                return new Comparison(VerdictWorse, $"{mine} kbps against the existing {theirs} kbps.{codecNote}");
            }
            return new Comparison(VerdictSimilar, $"{mine} kbps against the existing {theirs} kbps — about the same.{codecNote}");
        }

        private static string Hours(double seconds) =>
            (seconds / 3600).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " h";
    }
}
