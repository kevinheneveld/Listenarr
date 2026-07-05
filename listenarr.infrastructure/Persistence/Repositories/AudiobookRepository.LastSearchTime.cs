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
        public async Task SetLastSearchTimeAsync(int audiobookId, DateTime lastSearchTimeUtc)
        {
            // Targeted single-column write: must not load + re-save the entity,
            // or a stale snapshot would clobber concurrent changes to the row.
            // Uses a detached stub with only LastSearchTime marked modified
            // (rather than ExecuteUpdate) so the InMemory test provider runs the
            // same code path as SQLite — same rationale as
            // EfAudiobookFileRepository.ReassignAsync.
            var tracked = _db.Audiobooks.Local.FirstOrDefault(a => a.Id == audiobookId);
            if (tracked != null)
            {
                _db.Entry(tracked).State = EntityState.Detached;
            }

            var stub = new Audiobook { Id = audiobookId, LastSearchTime = lastSearchTimeUtc };
            var entry = _db.Entry(stub);
            entry.Property(a => a.LastSearchTime).IsModified = true;

            try
            {
                await _db.SaveChangesAsync();
            }
            finally
            {
                // Never leave a dirty stub behind for an unrelated later SaveChanges.
                entry.State = EntityState.Detached;
            }
        }
    }
}
