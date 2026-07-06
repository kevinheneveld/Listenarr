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

using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    public sealed partial class LibraryMaintenanceWorkflow
    {
        /// <summary>
        /// Merge phantom duplicate rows into their file-owning sibling: rows are
        /// clustered by ASIN and by (title, author, BasePath); in each cluster with
        /// exactly one file-owning "winner" and one or more zero-file "losers", the
        /// losers' downloads/history/move jobs are reassigned to the winner and the
        /// loser rows are deleted. Safety gates ported from kevin/live: ASIN clusters
        /// require matching title+author on every loser; title clusters refuse losers
        /// carrying a different ASIN; losers claimed by conflicting winners are skipped;
        /// clusters with multiple winners are the dedup tool's job, not this one's.
        /// </summary>
        public async Task<IActionResult> CleanupPhantomRowsAsync(bool dryRun, CancellationToken ct)
        {
            var allAudiobooks = await _repo.GetAllAsync();
            var fileCountByAudiobookId = await _fileRepository.GetCountsByAudiobookIdAsync(ct);

            var groups = new Dictionary<string, List<Audiobook>>(StringComparer.Ordinal);
            var groupKindByKey = new Dictionary<string, string>(StringComparer.Ordinal);

            void AddToGroup(Audiobook a, string key, string kind)
            {
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<Audiobook>();
                    groups[key] = list;
                    groupKindByKey[key] = kind;
                }
                list.Add(a);
            }

            foreach (var a in allAudiobooks)
            {
                var asin = (a.Asin ?? string.Empty).Trim().ToUpperInvariant();
                if (asin.Length > 0)
                {
                    AddToGroup(a, "ASIN:" + asin, "asin");
                }

                if (!string.IsNullOrWhiteSpace(a.Title) && !string.IsNullOrWhiteSpace(a.BasePath))
                {
                    var authorForKey = a.Authors?.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? string.Empty;
                    if (!string.IsNullOrWhiteSpace(authorForKey))
                    {
                        var titleKey = NormalizeForMatch(a.Title);
                        var authorKey = NormalizeForMatch(authorForKey);
                        var basePathKey = LibraryOrganizeSweepWorkflow.NormalizeOrganizePath(a.BasePath).TrimEnd('/', '\\').ToUpperInvariant();
                        if (!string.IsNullOrEmpty(titleKey) && !string.IsNullOrEmpty(authorKey) && !string.IsNullOrEmpty(basePathKey))
                        {
                            AddToGroup(a, "TAB:" + titleKey + "\0" + authorKey + "\0" + basePathKey, "titleAuthorBasePath");
                        }
                    }
                }
            }

            var skippedClusters = new List<object>();
            var loserClaim = new Dictionary<int, Audiobook>();
            var conflictingLoserIds = new HashSet<int>();
            var winnersById = new Dictionary<int, Audiobook>();

            foreach (var (key, members) in groups)
            {
                if (ct.IsCancellationRequested) break;
                if (members.Count < 2) continue; // not a cluster

                var kind = groupKindByKey.GetValueOrDefault(key, "unknown");
                var winners = members.Where(a => fileCountByAudiobookId.GetValueOrDefault(a.Id, 0) > 0).ToList();
                var losers = members.Where(a => fileCountByAudiobookId.GetValueOrDefault(a.Id, 0) == 0).ToList();

                if (winners.Count == 0) continue; // nobody owns files — outside this tool's scope
                if (losers.Count == 0) continue;  // everyone has files — the dedup tool's job
                if (winners.Count > 1)
                {
                    skippedClusters.Add(new
                    {
                        reason = "multiple_winners_in_cluster",
                        groupKind = kind,
                        title = members[0].Title,
                        basePath = members[0].BasePath,
                        asin = members[0].Asin,
                        winnerIds = winners.Select(w => w.Id).ToArray(),
                        loserIds = losers.Select(l => l.Id).ToArray(),
                    });
                    continue;
                }

                var winner = winners[0];
                var winnerAsin = (winner.Asin ?? string.Empty).Trim().ToUpperInvariant();
                var winnerTitleKey = NormalizeForMatch(winner.Title ?? string.Empty);
                var winnerAuthorKey = NormalizeForMatch(winner.Authors?.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? string.Empty);

                var safeLosers = new List<Audiobook>();
                foreach (var loser in losers)
                {
                    var loserAsin = (loser.Asin ?? string.Empty).Trim().ToUpperInvariant();

                    if (kind == "asin")
                    {
                        var loserTitleKey = NormalizeForMatch(loser.Title ?? string.Empty);
                        var loserAuthorKey = NormalizeForMatch(loser.Authors?.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s)) ?? string.Empty);
                        if (loserTitleKey != winnerTitleKey || loserAuthorKey != winnerAuthorKey)
                        {
                            skippedClusters.Add(new
                            {
                                reason = "asin_match_but_title_or_author_differs",
                                asin = winnerAsin,
                                winnerId = winner.Id,
                                winnerTitle = winner.Title,
                                loserId = loser.Id,
                                loserTitle = loser.Title,
                            });
                            continue;
                        }
                    }
                    else if (loserAsin.Length > 0 && loserAsin != winnerAsin)
                    {
                        skippedClusters.Add(new
                        {
                            reason = "loser_asin_mismatch",
                            title = loser.Title,
                            winnerId = winner.Id,
                            winnerAsin,
                            loserId = loser.Id,
                            loserAsin,
                        });
                        continue;
                    }
                    safeLosers.Add(loser);
                }

                if (safeLosers.Count > 0)
                {
                    winnersById[winner.Id] = winner;
                    foreach (var loser in safeLosers)
                    {
                        if (loserClaim.TryGetValue(loser.Id, out var existingWinner))
                        {
                            if (existingWinner.Id != winner.Id)
                            {
                                conflictingLoserIds.Add(loser.Id);
                                skippedClusters.Add(new
                                {
                                    reason = "conflicting_winners",
                                    loserId = loser.Id,
                                    title = loser.Title,
                                    winnerIdA = existingWinner.Id,
                                    winnerIdB = winner.Id,
                                });
                            }
                        }
                        else
                        {
                            loserClaim[loser.Id] = winner;
                        }
                    }
                }
            }

            foreach (var conflictId in conflictingLoserIds)
            {
                loserClaim.Remove(conflictId);
            }

            var plannedMerges = loserClaim
                .GroupBy(kv => kv.Value.Id)
                .Select(g => (Winner: winnersById[g.Key], LoserIds: g.Select(kv => kv.Key).ToList()))
                .ToList();

            var mergeDetails = plannedMerges
                .Select(m => new { winnerId = m.Winner.Id, title = m.Winner.Title, basePath = m.Winner.BasePath, loserIds = m.LoserIds.ToArray() })
                .ToList();

            if (dryRun)
            {
                return new OkObjectResult(new
                {
                    dryRun = true,
                    clustersInspected = groups.Count,
                    merges = plannedMerges.Count,
                    losersToDelete = plannedMerges.Sum(m => m.LoserIds.Count),
                    skippedClusters = skippedClusters.Count,
                    mergeDetails,
                    skippedDetails = skippedClusters,
                });
            }

            int downloadsReassigned = 0, historyReassigned = 0, moveJobsReassigned = 0, rowsDeleted = 0;
            var executionErrors = new List<object>();

            foreach (var (winner, loserIds) in plannedMerges)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    var counts = await _repo.MergeAudiobookRowsAsync(winner.Id, loserIds, ct);
                    downloadsReassigned += counts.DownloadsReassigned;
                    historyReassigned += counts.HistoryReassigned;
                    moveJobsReassigned += counts.MoveJobsReassigned;
                    rowsDeleted += counts.RowsDeleted;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogError(ex, "Phantom cleanup: merge failed for winner {WinnerId} losers [{LoserIds}]", winner.Id, string.Join(",", loserIds));
                    executionErrors.Add(new { winnerId = winner.Id, loserIds, error = ex.Message });
                }
            }

            _logger.LogInformation(
                "Phantom cleanup applied: {Merges} merges, {Deleted} rows deleted, {Downloads} downloads reassigned, {History} history reassigned, {MoveJobs} move jobs reassigned, {Errors} errors",
                plannedMerges.Count, rowsDeleted, downloadsReassigned, historyReassigned, moveJobsReassigned, executionErrors.Count);

            return new OkObjectResult(new
            {
                dryRun = false,
                clustersInspected = groups.Count,
                merges = plannedMerges.Count,
                rowsDeleted,
                downloadsReassigned,
                historyReassigned,
                moveJobsReassigned,
                skippedClusters = skippedClusters.Count,
                executionErrors,
                skippedDetails = skippedClusters,
            });
        }

        /// <summary>Letters+digits, lowercased — punctuation/spacing-tolerant matching.</summary>
        private static string NormalizeForMatch(string s)
        {
            if (string.IsNullOrEmpty(s)) return string.Empty;
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (var ch in s)
            {
                if (char.IsLetterOrDigit(ch)) sb.Append(char.ToLowerInvariant(ch));
            }
            return sb.ToString();
        }
    }
}
