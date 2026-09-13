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
    public class EfSeriesTriageDecisionRepository : ISeriesTriageDecisionRepository
    {
        private readonly ListenArrDbContext _db;

        public EfSeriesTriageDecisionRepository(ListenArrDbContext db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public async Task<List<SeriesTriageDecision>> GetAllAsync(CancellationToken ct = default)
        {
            return await _db.SeriesTriageDecisions.AsNoTracking().ToListAsync(ct);
        }

        public async Task<SeriesTriageDecision> UpsertAsync(SeriesTriageDecision decision, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(decision);
            var existing = await _db.SeriesTriageDecisions
                .FirstOrDefaultAsync(d => d.SeriesNameNormalized == decision.SeriesNameNormalized, ct);

            if (existing == null)
            {
                _db.SeriesTriageDecisions.Add(decision);
                await _db.SaveChangesAsync(ct);
                return decision;
            }

            existing.SeriesName = decision.SeriesName;
            existing.SeriesAsin = decision.SeriesAsin ?? existing.SeriesAsin;
            existing.Decision = decision.Decision;
            existing.Note = decision.Note;
            existing.CreatedAt = decision.CreatedAt;
            await _db.SaveChangesAsync(ct);
            return existing;
        }

        public async Task<bool> DeleteByNormalizedNameAsync(string normalizedName, CancellationToken ct = default)
        {
            var rows = await _db.SeriesTriageDecisions
                .Where(d => d.SeriesNameNormalized == normalizedName)
                .ToListAsync(ct);
            if (rows.Count == 0)
            {
                return false;
            }

            _db.SeriesTriageDecisions.RemoveRange(rows);
            await _db.SaveChangesAsync(ct);
            return true;
        }
    }
}
