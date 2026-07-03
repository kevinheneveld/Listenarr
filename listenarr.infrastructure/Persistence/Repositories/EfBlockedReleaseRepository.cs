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
using Listenarr.Domain.Search;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Repositories
{
    public class EfBlockedReleaseRepository : IBlockedReleaseRepository
    {
        private readonly ListenArrDbContext _db;

        public EfBlockedReleaseRepository(ListenArrDbContext db)
        {
            _db = db ?? throw new ArgumentNullException(nameof(db));
        }

        public async Task AddAsync(BlockedRelease blockedRelease, CancellationToken ct = default)
        {
            _db.BlockedReleases.Add(blockedRelease);
            await _db.SaveChangesAsync(ct);
        }

        public async Task<List<BlockedRelease>> GetByAudiobookIdAsync(int audiobookId, CancellationToken ct = default)
        {
            return await _db.BlockedReleases
                .AsNoTracking()
                .Where(b => b.AudiobookId == audiobookId)
                .ToListAsync(ct);
        }
    }
}
