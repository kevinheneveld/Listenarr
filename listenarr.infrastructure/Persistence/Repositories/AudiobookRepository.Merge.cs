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

using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Repositories
{
    public partial class AudiobookRepository
    {
        /// <summary>
        /// Absorb duplicate rows into a winner: reassign the losers' downloads,
        /// history, and move jobs to the winner, then delete the loser rows.
        /// Transactional where the provider supports it (the InMemory test
        /// provider does not — there the tracked-entity path runs instead,
        /// exercising identical semantics). Loser AudiobookFile rows go with
        /// their parent via cascade; the merge flows delete losers' files from
        /// disk beforehand.
        /// </summary>
        public async Task<AudiobookMergeCounts> MergeAudiobookRowsAsync(
            int winnerId,
            IReadOnlyCollection<int> loserIds,
            CancellationToken ct = default)
        {
            var counts = new AudiobookMergeCounts();
            var losers = loserIds.Distinct().Where(id => id != winnerId).ToList();
            if (losers.Count == 0) return counts;

            var supportsTransactions = !string.Equals(
                _db.Database.ProviderName,
                "Microsoft.EntityFrameworkCore.InMemory",
                StringComparison.Ordinal);

            if (supportsTransactions)
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                counts.DownloadsReassigned = await _db.Downloads
                    .Where(d => d.AudiobookId != null && losers.Contains(d.AudiobookId.Value))
                    .ExecuteUpdateAsync(s => s.SetProperty(d => d.AudiobookId, winnerId), ct);
                counts.HistoryReassigned = await _db.History
                    .Where(h => h.AudiobookId != null && losers.Contains(h.AudiobookId.Value))
                    .ExecuteUpdateAsync(s => s.SetProperty(h => h.AudiobookId, winnerId), ct);
                counts.MoveJobsReassigned = await _db.MoveJobs
                    .Where(j => losers.Contains(j.AudiobookId))
                    .ExecuteUpdateAsync(s => s.SetProperty(j => j.AudiobookId, winnerId), ct);
                counts.RowsDeleted = await _db.Audiobooks
                    .Where(a => losers.Contains(a.Id))
                    .ExecuteDeleteAsync(ct);
                await tx.CommitAsync(ct);
                return counts;
            }

            var downloads = await _db.Downloads
                .Where(d => d.AudiobookId != null && losers.Contains(d.AudiobookId.Value))
                .ToListAsync(ct);
            foreach (var d in downloads) d.AudiobookId = winnerId;
            counts.DownloadsReassigned = downloads.Count;

            var historyRows = await _db.History
                .Where(h => h.AudiobookId != null && losers.Contains(h.AudiobookId.Value))
                .ToListAsync(ct);
            foreach (var h in historyRows) h.AudiobookId = winnerId;
            counts.HistoryReassigned = historyRows.Count;

            var moveJobs = await _db.MoveJobs
                .Where(j => losers.Contains(j.AudiobookId))
                .ToListAsync(ct);
            foreach (var j in moveJobs) j.AudiobookId = winnerId;
            counts.MoveJobsReassigned = moveJobs.Count;

            var loserRows = await _db.Audiobooks
                .Where(a => losers.Contains(a.Id))
                .ToListAsync(ct);
            _db.Audiobooks.RemoveRange(loserRows);
            counts.RowsDeleted = loserRows.Count;

            await _db.SaveChangesAsync(ct);
            return counts;
        }

        /// <summary>
        /// Null out the ASIN on the given rows — the "these share an ASIN but are
        /// actually different books" escape hatch that stops the duplicate scan
        /// from flagging them again.
        /// </summary>
        public async Task<int> ClearAsinsAsync(IReadOnlyCollection<int> audiobookIds, CancellationToken ct = default)
        {
            var ids = audiobookIds.Distinct().ToList();
            if (ids.Count == 0) return 0;

            var supportsTransactions = !string.Equals(
                _db.Database.ProviderName,
                "Microsoft.EntityFrameworkCore.InMemory",
                StringComparison.Ordinal);

            if (supportsTransactions)
            {
                return await _db.Audiobooks
                    .Where(a => ids.Contains(a.Id))
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.Asin, (string?)null), ct);
            }

            var rows = await _db.Audiobooks.Where(a => ids.Contains(a.Id)).ToListAsync(ct);
            foreach (var r in rows) r.Asin = null;
            await _db.SaveChangesAsync(ct);
            return rows.Count;
        }
    }
}
