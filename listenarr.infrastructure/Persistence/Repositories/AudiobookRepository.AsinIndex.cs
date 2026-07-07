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
        /// Best-effort creation of the database-level unique-ASIN backstop
        /// (upstream issue #6). The in-process AudiobookAddLockManager already
        /// serializes same-ASIN adds within one process; this index is the
        /// cross-process/cross-writer guarantee.
        ///
        /// Deliberately NOT an EF migration: `CREATE UNIQUE INDEX` fails
        /// outright on databases that already contain duplicate ASINs, and a
        /// failed migration blocks every later migration on that install. Run
        /// from startup instead (idempotent, every boot): clean databases get
        /// the index; dirty ones get a warning naming the duplicate count and
        /// self-heal on the boot after the user merges their duplicates
        /// (Settings → General → Duplicates).
        /// </summary>
        public async Task<AsinIndexEnsureResult> EnsureAsinUniqueIndexAsync(CancellationToken ct = default)
        {
            if (_db.Database.ProviderName?.Contains("InMemory", StringComparison.OrdinalIgnoreCase) == true)
            {
                return new AsinIndexEnsureResult { Outcome = AsinIndexOutcome.SkippedNonRelational };
            }

            // Provider-agnostic duplicate probe (LINQ, not raw SQL) so the
            // decision logic is testable and can't drift from the data.
            var duplicateGroups = await _db.Audiobooks
                .AsNoTracking()
                .Where(a => a.Asin != null && a.Asin != "")
                .GroupBy(a => a.Asin)
                .Where(g => g.Count() > 1)
                .CountAsync(ct);

            if (duplicateGroups > 0)
            {
                return new AsinIndexEnsureResult
                {
                    Outcome = AsinIndexOutcome.SkippedDuplicatesExist,
                    DuplicateAsinGroups = duplicateGroups,
                };
            }

            // Partial index: NULL/empty ASINs stay unconstrained (wishlist and
            // scan-created rows legitimately lack one).
            await _db.Database.ExecuteSqlRawAsync(
                "CREATE UNIQUE INDEX IF NOT EXISTS \"IX_Audiobooks_Asin_Unique\" " +
                "ON \"Audiobooks\" (\"Asin\") WHERE \"Asin\" IS NOT NULL AND \"Asin\" <> '';",
                ct);

            return new AsinIndexEnsureResult { Outcome = AsinIndexOutcome.Ensured };
        }
    }
}
