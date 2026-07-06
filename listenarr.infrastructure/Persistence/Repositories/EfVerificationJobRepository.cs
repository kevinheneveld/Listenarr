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
    public class EfVerificationJobRepository : IVerificationJobRepository
    {
        private readonly ListenArrDbContext _db;

        public EfVerificationJobRepository(ListenArrDbContext db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public async Task AddAsync(VerificationJobRecord record, CancellationToken ct = default)
        {
            _db.VerificationJobs.Add(record);
            await _db.SaveChangesAsync(ct);
        }

        public async Task SetStatusAsync(Guid id, string status, CancellationToken ct = default)
        {
            var record = await _db.VerificationJobs.FindAsync(new object[] { id }, ct);
            if (record == null) return;
            record.Status = status;
            if (status is "Completed" or "Failed" or "Cancelled")
            {
                record.CompletedAt = DateTime.UtcNow;
            }
            await _db.SaveChangesAsync(ct);
        }

        public async Task<List<VerificationJobRecord>> GetPendingAsync(CancellationToken ct = default)
        {
            return await _db.VerificationJobs
                .AsNoTracking()
                .Where(r => r.Status == "Queued" || r.Status == "Processing")
                .OrderBy(r => r.EnqueuedAt)
                .ToListAsync(ct);
        }

        public async Task<List<VerificationJobRecord>> GetRecentCompletedAsync(int take, CancellationToken ct = default)
        {
            return await _db.VerificationJobs
                .AsNoTracking()
                .Where(r => r.Status == "Completed" && r.CompletedAt != null)
                .OrderByDescending(r => r.CompletedAt)
                .Take(take)
                .ToListAsync(ct);
        }

        public async Task<List<VerificationJobRecord>> GetCompletedSinceAsync(DateTime sinceUtc, CancellationToken ct = default)
        {
            return await _db.VerificationJobs
                .AsNoTracking()
                .Where(r => r.Status == "Completed" && r.CompletedAt != null && r.CompletedAt >= sinceUtc)
                .ToListAsync(ct);
        }

        public async Task DeleteFinishedOlderThanAsync(DateTime cutoffUtc, CancellationToken ct = default)
        {
            var stale = await _db.VerificationJobs
                .Where(r => (r.Status == "Completed" || r.Status == "Failed" || r.Status == "Cancelled")
                            && (r.CompletedAt ?? r.EnqueuedAt) < cutoffUtc)
                .ToListAsync(ct);
            if (stale.Count == 0) return;
            _db.VerificationJobs.RemoveRange(stale);
            await _db.SaveChangesAsync(ct);
        }
    }
}
