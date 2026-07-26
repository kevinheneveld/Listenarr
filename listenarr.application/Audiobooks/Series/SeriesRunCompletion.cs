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
using Listenarr.Domain.Audiobooks;

namespace Listenarr.Application.Audiobooks.Series
{
    /// <summary>
    /// The "one more grab finishes it" number: how complete the user's BEST
    /// recording edition of a series is. A series where a single narrator
    /// run is 8/9 owned must rank above one where 8 owned books scatter
    /// across three incompatible runs — overall owned/total can't see the
    /// difference, so prioritization needs the per-run view.
    /// </summary>
    public static class SeriesRunCompletion
    {
        public sealed record BestRunSummary(
            string Label,
            string Kind,
            IReadOnlyList<string> Narrators,
            int OwnedWorks,
            int TotalWorks)
        {
            public double Completion => TotalWorks > 0 ? (double)OwnedWorks / TotalWorks : 0;
        }

        /// <summary>
        /// Picks the run holding the MOST owned works — the shelf the user
        /// is actually building — with owned-fraction, then size, as
        /// tiebreaks. Ranking by fraction alone let a fully-owned 3-book
        /// subset run outrank the 8/9 main edition, hiding exactly the
        /// "one more grab finishes it" series this exists to surface.
        /// Runs covering a single work are ignored unless nothing better
        /// exists — a lone omnibus "run" at 1/1 must not report a nine-book
        /// series as complete.
        /// </summary>
        public static BestRunSummary? BestRun(
            IReadOnlyList<CachedSeriesCatalogBook> entries,
            IReadOnlyCollection<string> ownedWorkKeys)
        {
            if (entries.Count == 0) return null;

            var runs = RecordingEditionClassifier.GroupIntoRuns(
                entries,
                e => RecordingEditionClassifier.Classify(e.Title, e.Subtitle, e.Narrators, e.Publisher, e.PublishedDate));

            BestRunSummary? best = null;
            BestRunSummary? bestSingle = null;
            foreach (var run in runs)
            {
                var workKeys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (entry, _) in run.Entries)
                {
                    var key = SeriesWorkKey.Build(entry.Title, entry.Authors, entry.SeriesNumber);
                    if (key.Length > 0) workKeys.Add(key);
                }
                if (workKeys.Count == 0) continue;

                var summary = new BestRunSummary(
                    run.Label,
                    run.Kind.ToString(),
                    run.Narrators,
                    workKeys.Count(ownedWorkKeys.Contains),
                    workKeys.Count);

                ref var slot = ref (workKeys.Count == 1 ? ref bestSingle : ref best);
                if (slot == null
                    || summary.OwnedWorks > slot.OwnedWorks
                    || (summary.OwnedWorks == slot.OwnedWorks && summary.Completion > slot.Completion)
                    || (summary.OwnedWorks == slot.OwnedWorks && summary.Completion == slot.Completion && summary.TotalWorks > slot.TotalWorks))
                {
                    slot = summary;
                }
            }

            return best ?? bestSingle;
        }
    }
}
