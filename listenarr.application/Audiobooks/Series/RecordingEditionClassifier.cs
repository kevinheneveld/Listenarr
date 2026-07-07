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
using System.Globalization;
using System.Text.RegularExpressions;

namespace Listenarr.Application.Audiobooks.Series
{
    public enum RecordingEditionKind
    {
        Standard = 0,
        Dramatized = 1,
        GraphicAudio = 2,
        RadioDrama = 3,
        Abridged = 4
    }

    /// <summary>One catalog recording, classified.</summary>
    public sealed record RecordingEdition(
        RecordingEditionKind Kind,
        string NarratorKey,
        IReadOnlyList<string> Narrators,
        string? Publisher,
        DateTime? ReleaseDate);

    /// <summary>A group of recordings judged to be the same production run.</summary>
    public sealed class RecordingRun<T>
    {
        public RecordingEditionKind Kind { get; init; }
        public string Label { get; set; } = string.Empty;
        public List<string> Narrators { get; } = new();
        public List<(T Entry, RecordingEdition Edition)> Entries { get; } = new();
        public string? Publisher { get; set; }
        public DateTime? EarliestRelease { get; set; }
        public DateTime? LatestRelease { get; set; }
    }

    /// <summary>
    /// HEURISTIC grouping of a series' catalog recordings into production
    /// "runs" — a narration run (R.C. Bray reading books 1–3) is a different
    /// run from a full-cast dramatization of the same works, and gap-filling
    /// should not mix them. Labeled a heuristic on purpose: Audible publishes
    /// no run identifier, so this classifies from title/subtitle markers and
    /// groups by narrator set, allowing legitimate mid-series narrator
    /// switches (same kind + same publisher + overlapping release era).
    /// </summary>
    public static class RecordingEditionClassifier
    {
        private static readonly Regex GraphicAudioRegex = new(
            @"\bgraphic\s*audio\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex RadioDramaRegex = new(
            @"\bradio\s+(drama|play)\b|\bbbc\s+radio\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex DramatizedRegex = new(
            @"\bfull[\s-]?cast\b|\bdramati[sz](ed|ation)\b|\btheatrical\b|\bstage\s+production\b|\bsoundscape\b|\baudio\s+drama\b",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex AbridgedRegex = new(
            @"(?<!un)abridged", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // Release eras within this window (beyond the run's own span) are
        // considered the same run when the publisher matches — series
        // recordings ship over years, and narrator switches mid-series are
        // legitimate (Kevin's explicit requirement).
        private const int NarratorSwitchToleranceYears = 2;

        public static RecordingEdition Classify(
            string? title,
            string? subtitle,
            IReadOnlyList<string>? narrators,
            string? publisher,
            string? releaseDate)
        {
            var text = $"{title} {subtitle}";
            var narratorList = (narrators ?? Array.Empty<string>())
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim())
                .ToList();
            var narratorText = string.Join(" ", narratorList);

            RecordingEditionKind kind;
            if (GraphicAudioRegex.IsMatch(text) || GraphicAudioRegex.IsMatch(publisher ?? string.Empty))
            {
                kind = RecordingEditionKind.GraphicAudio;
            }
            else if (RadioDramaRegex.IsMatch(text))
            {
                kind = RecordingEditionKind.RadioDrama;
            }
            else if (DramatizedRegex.IsMatch(text) || DramatizedRegex.IsMatch(narratorText))
            {
                kind = RecordingEditionKind.Dramatized;
            }
            else if (AbridgedRegex.IsMatch(text))
            {
                kind = RecordingEditionKind.Abridged;
            }
            else
            {
                kind = RecordingEditionKind.Standard;
            }

            var narratorKey = string.Join("|",
                narratorList.Select(SeriesWorkKey.NormalizeText)
                    .Where(n => n.Length > 0)
                    .OrderBy(n => n, StringComparer.Ordinal));

            return new RecordingEdition(kind, narratorKey, narratorList, publisher?.Trim(), ParseReleaseDate(releaseDate));
        }

        /// <summary>
        /// Groups classified entries into runs: same kind + same narrator set is
        /// one run; same kind + different narrators merges only when the
        /// publisher matches AND the release eras overlap within the tolerance
        /// window (the mid-series narrator-switch case). Entries lacking both
        /// publisher and dates stay in their narrator-set group — conservative.
        /// </summary>
        public static List<RecordingRun<T>> GroupIntoRuns<T>(
            IEnumerable<T> entries,
            Func<T, RecordingEdition> classify)
        {
            // Seed: one group per (kind, narratorKey).
            var seeds = new List<RecordingRun<T>>();
            foreach (var entry in entries)
            {
                var edition = classify(entry);
                var run = seeds.FirstOrDefault(r =>
                    r.Kind == edition.Kind
                    && r.Entries.Count > 0
                    && r.Entries[0].Edition.NarratorKey == edition.NarratorKey);
                if (run == null)
                {
                    run = new RecordingRun<T> { Kind = edition.Kind };
                    seeds.Add(run);
                }

                run.Entries.Add((entry, edition));
                foreach (var narrator in edition.Narrators)
                {
                    if (!run.Narrators.Contains(narrator, StringComparer.OrdinalIgnoreCase))
                    {
                        run.Narrators.Add(narrator);
                    }
                }

                run.Publisher ??= edition.Publisher;
                if (edition.ReleaseDate is { } d)
                {
                    if (run.EarliestRelease is not { } e || d < e) run.EarliestRelease = d;
                    if (run.LatestRelease is not { } l || d > l) run.LatestRelease = d;
                }
            }

            // Merge pass: narrator switches within one production run.
            var merged = new List<RecordingRun<T>>();
            foreach (var run in seeds.OrderByDescending(r => r.Entries.Count))
            {
                var target = merged.FirstOrDefault(m => CanMerge(m, run));
                if (target == null)
                {
                    merged.Add(run);
                    continue;
                }

                foreach (var e in run.Entries) target.Entries.Add(e);
                foreach (var narrator in run.Narrators)
                {
                    if (!target.Narrators.Contains(narrator, StringComparer.OrdinalIgnoreCase))
                    {
                        target.Narrators.Add(narrator);
                    }
                }

                if (run.EarliestRelease is { } re && (target.EarliestRelease is not { } te || re < te))
                {
                    target.EarliestRelease = re;
                }

                if (run.LatestRelease is { } rl && (target.LatestRelease is not { } tl || rl > tl))
                {
                    target.LatestRelease = rl;
                }
            }

            foreach (var run in merged)
            {
                run.Label = BuildLabel(run.Kind, run.Narrators, run.Publisher);
            }

            return merged
                .OrderByDescending(r => r.Entries.Count)
                .ThenBy(r => r.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool CanMerge<T>(RecordingRun<T> a, RecordingRun<T> b)
        {
            if (a.Kind != b.Kind) return false;

            // Same narrator set was already grouped at seed time; this pass is
            // only for narrator switches, which require publisher corroboration.
            if (string.IsNullOrWhiteSpace(a.Publisher) || string.IsNullOrWhiteSpace(b.Publisher)) return false;
            if (!string.Equals(
                    SeriesWorkKey.NormalizeText(a.Publisher),
                    SeriesWorkKey.NormalizeText(b.Publisher),
                    StringComparison.Ordinal))
            {
                return false;
            }

            // Release eras must be known on both sides and close enough.
            if (a.EarliestRelease is not { } aStart || a.LatestRelease is not { } aEnd) return false;
            if (b.EarliestRelease is not { } bStart || b.LatestRelease is not { } bEnd) return false;

            var tolerance = TimeSpan.FromDays(365.25 * NarratorSwitchToleranceYears);
            return bStart <= aEnd + tolerance && aStart <= bEnd + tolerance;
        }

        private static string BuildLabel(RecordingEditionKind kind, IReadOnlyList<string> narrators, string? publisher)
        {
            var narratorPart = narrators.Count switch
            {
                0 => string.Empty,
                1 => narrators[0],
                2 => $"{narrators[0]} & {narrators[1]}",
                _ => $"{narrators[0]} & {narrators.Count - 1} others"
            };

            return kind switch
            {
                RecordingEditionKind.GraphicAudio => "Full Cast (GraphicAudio)",
                RecordingEditionKind.RadioDrama => "Radio Drama",
                RecordingEditionKind.Dramatized => narratorPart.Length > 0 ? $"Full Cast — {narratorPart}" : "Full Cast / Dramatized",
                RecordingEditionKind.Abridged => narratorPart.Length > 0 ? $"Abridged — Narrated by {narratorPart}" : "Abridged",
                _ => narratorPart.Length > 0 ? $"Narrated by {narratorPart}" : "Unknown narrator"
            };
        }

        private static DateTime? ParseReleaseDate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return null;
            if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed))
            {
                return parsed;
            }

            // Year-only strings ("2014") are common in catalog data.
            if (int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var year)
                && year is > 1900 and < 2200)
            {
                return new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            }

            return null;
        }
    }
}
