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
    /// Resolution logic on top of the duplicate-copy detector: given a record's
    /// file clusters, propose WHICH cluster to keep and which are redundant
    /// copies of the same audio. Deterministic and evidence-graded:
    /// total-duration agreement pairs clusters as copies, byte-size agreement
    /// marks candidates for hash confirmation (done by the caller — this class
    /// never touches the filesystem), and the keeper is the highest-bitrate
    /// copy. Proposals are advisory; nothing here mutates anything.
    /// </summary>
    public static class DuplicateCopyAnalyzer
    {
        /// <summary>A cluster's measurable identity, prepared by the caller.</summary>
        public sealed record ClusterEvidence(
            string Key,
            string DisplayName,
            IReadOnlyList<int> FileIds,
            int FileCount,
            long TotalBytes,
            double TotalDurationSeconds,
            bool DurationsComplete,
            IReadOnlyList<long> SortedFileSizes);

        /// <summary>One resolution proposal: keep one cluster, the rest are copies.</summary>
        public sealed record CopyProposal(
            ClusterEvidence Keeper,
            IReadOnlyList<ClusterEvidence> Redundant,
            string Confidence,
            IReadOnlyList<string> Evidence,
            bool SizesIdentical,
            long ReclaimableBytes);

        /// <summary>Record-level outcome: keep/drop proposals plus a signal
        /// that the clusters look like DIFFERENT books sharing one record
        /// (loose runtime kinship or conflicting numbers in the names) — a
        /// Split Collection candidate, never a dedupe target.</summary>
        public sealed record AnalysisResult(
            IReadOnlyList<CopyProposal> Proposals,
            bool SplitCandidate);

        public const string ConfidenceHigh = "high";
        public const string ConfidenceReview = "review";
        public const string ConfidenceIdentical = "identical";

        // Two clusters whose total runtimes agree within 2% are the same audio
        // for practical purposes (different rips of one narration drift by
        // encoder padding, not minutes). Down to 75% they are only POSSIBLY
        // related — first live run showed that band is where different books
        // of one series masquerade as copies (a 12.2h Foundation's Edge nearly
        // paired a 16.1h Forward the Foundation), so loose kinship now signals
        // "split this record", never a keep/drop proposal.
        private const double TightDurationRatio = 0.98;
        private const double LooseDurationRatio = 0.75;

        // A cluster must carry at least this much audio to participate — the
        // first live run united 189 six-MB "Part NNN of" stubs with agreeing
        // 12-minute durations into one absurd keep-one-drop-188 proposal.
        private const double MinClusterDurationSeconds = 1800;

        public static AnalysisResult Analyze(IReadOnlyList<ClusterEvidence> clusters)
        {
            var eligible = clusters
                .Where(c => c.FileCount > 0
                    && (!c.DurationsComplete || c.TotalDurationSeconds >= MinClusterDurationSeconds))
                .ToList();
            if (eligible.Count < 2) return new AnalysisResult([], false);

            // Union clusters into copy-groups via pairwise duration agreement.
            var splitCandidate = false;
            var groupOf = eligible.ToDictionary(c => c, _ => -1);
            var groups = new List<List<ClusterEvidence>>();
            for (var i = 0; i < eligible.Count; i++)
            {
                for (var j = i + 1; j < eligible.Count; j++)
                {
                    var pair = ClassifyPair(eligible[i], eligible[j]);
                    if (pair == PairKind.SplitKin)
                    {
                        splitCandidate = true;
                        continue;
                    }
                    if (pair == PairKind.Unrelated) continue;

                    var gi = groupOf[eligible[i]];
                    var gj = groupOf[eligible[j]];
                    if (gi < 0 && gj < 0)
                    {
                        groups.Add([eligible[i], eligible[j]]);
                        groupOf[eligible[i]] = groupOf[eligible[j]] = groups.Count - 1;
                    }
                    else if (gi < 0)
                    {
                        groups[gj].Add(eligible[i]);
                        groupOf[eligible[i]] = gj;
                    }
                    else if (gj < 0)
                    {
                        groups[gi].Add(eligible[j]);
                        groupOf[eligible[j]] = gi;
                    }
                    else if (gi != gj)
                    {
                        groups[gi].AddRange(groups[gj]);
                        foreach (var moved in groups[gj]) groupOf[moved] = gi;
                        groups[gj] = [];
                    }
                }
            }

            var proposals = groups
                .Where(g => g.Count >= 2)
                .Select(BuildProposal)
                .OrderByDescending(p => p.ReclaimableBytes)
                .ToList();
            return new AnalysisResult(proposals, splitCandidate);
        }

        private enum PairKind { Unrelated, Copies, SplitKin }

        private static PairKind ClassifyPair(ClusterEvidence a, ClusterEvidence b)
        {
            // Conflicting numbers in the cluster names ("Book 008" vs
            // "Book 009") mean sibling volumes, never copies — no runtime
            // agreement overrides that (Odd Apocalypse's two 5.4h halves
            // taught that lesson from the other direction).
            var digitConflict =
                SplitDestinationSuggester.DigitsConflict(a.DisplayName, b.DisplayName);

            var exactSizes = a.FileCount == b.FileCount
                && a.SortedFileSizes.SequenceEqual(b.SortedFileSizes);

            if (a.DurationsComplete && b.DurationsComplete
                && a.TotalDurationSeconds > 0 && b.TotalDurationSeconds > 0)
            {
                var ratio = Math.Min(a.TotalDurationSeconds, b.TotalDurationSeconds)
                            / Math.Max(a.TotalDurationSeconds, b.TotalDurationSeconds);
                if (ratio >= TightDurationRatio && !digitConflict)
                {
                    return PairKind.Copies;
                }
                // Byte-identical files are the same audio no matter what the
                // folder names claim — a mislabeled copy is still a copy.
                if (exactSizes) return PairKind.Copies;
                return ratio >= LooseDurationRatio || (ratio >= TightDurationRatio && digitConflict)
                    ? PairKind.SplitKin
                    : PairKind.Unrelated;
            }

            // Without trustworthy durations only exact byte identity is
            // acceptable evidence: same file count, same sorted size multiset.
            return exactSizes ? PairKind.Copies : PairKind.Unrelated;
        }

        private static CopyProposal BuildProposal(List<ClusterEvidence> group)
        {
            // Keeper: highest effective bitrate (bytes per second of audio);
            // ties go to the more chapterized copy, then the stable key order.
            var keeper = group
                .OrderByDescending(c => c.DurationsComplete && c.TotalDurationSeconds > 0
                    ? c.TotalBytes / c.TotalDurationSeconds
                    : c.TotalBytes)
                .ThenByDescending(c => c.FileCount)
                .ThenBy(c => c.Key, StringComparer.Ordinal)
                .First();
            var redundant = group.Where(c => c != keeper).ToList();

            var evidence = new List<string>();
            var allTight = true;
            var sizesIdentical = true;
            foreach (var r in redundant)
            {
                if (keeper.DurationsComplete && r.DurationsComplete
                    && keeper.TotalDurationSeconds > 0 && r.TotalDurationSeconds > 0)
                {
                    var ratio = Math.Min(keeper.TotalDurationSeconds, r.TotalDurationSeconds)
                                / Math.Max(keeper.TotalDurationSeconds, r.TotalDurationSeconds);
                    evidence.Add(
                        $"'{r.DisplayName}' runtime {FormatHours(r.TotalDurationSeconds)} vs keeper {FormatHours(keeper.TotalDurationSeconds)} ({ratio:P1} agreement)");
                    if (ratio < TightDurationRatio) allTight = false;
                }
                else
                {
                    evidence.Add($"'{r.DisplayName}' has incomplete duration metadata");
                    allTight = false;
                }

                if (r.FileCount == keeper.FileCount && r.SortedFileSizes.SequenceEqual(keeper.SortedFileSizes))
                {
                    evidence.Add($"'{r.DisplayName}' file sizes are byte-identical to the keeper's");
                }
                else
                {
                    sizesIdentical = false;
                }
            }

            return new CopyProposal(
                keeper,
                redundant,
                allTight ? ConfidenceHigh : ConfidenceReview,
                evidence,
                sizesIdentical,
                redundant.Sum(r => r.TotalBytes));
        }

        private static string FormatHours(double seconds) => $"{seconds / 3600:0.0}h";
    }
}
