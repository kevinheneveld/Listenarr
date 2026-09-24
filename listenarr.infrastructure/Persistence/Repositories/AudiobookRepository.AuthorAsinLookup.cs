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

using Listenarr.Application.Audiobooks;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Repositories
{
    // Name → author-ASIN resolution through the library's own records, split out of
    // AudiobookRepository.cs to keep it under the architecture size cap.
    public partial class AudiobookRepository
    {
        public async Task<string?> GetAuthorAsinByNameAsync(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;

            var target = NormalizeAuthorName(name);

            // Materialize first because SQLite cannot translate list-property checks on our JSON-backed columns.
            var candidates = await _db.Audiobooks
                .AsNoTracking()
                .ToListAsync();

            // A book's stored ASIN can belong to a DIFFERENT author than its current
            // Authors: relabelling a wrong grab rewrote Authors but left the ASIN
            // resolved for the old author (live: "Fonda Lee" resolved to James
            // Patterson's ASIN through one relabelled Alex Cross record, and the
            // author page rendered Patterson). Only trust an ASIN the author cache
            // doesn't contradict, and prefer single-author books, where ASIN[0]
            // unambiguously belongs to the requested name.
            var cachedNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var cacheRows = await _db.AuthorCacheEntries
                .AsNoTracking()
                .Where(entry => entry.AuthorAsin != null)
                .Select(entry => new { entry.AuthorAsin, entry.AuthorName })
                .ToListAsync();
            foreach (var row in cacheRows)
            {
                if (!string.IsNullOrWhiteSpace(row.AuthorAsin) && !cachedNames.ContainsKey(row.AuthorAsin))
                {
                    cachedNames[row.AuthorAsin] = row.AuthorName;
                }
            }

            var matching = candidates
                .Where(b => b.AuthorAsins != null && b.AuthorAsins.Count > 0 && b.Authors != null && b.Authors.Count > 0)
                .Where(b => b.Authors!.Any(a => NormalizeAuthorName(a) == target))
                .OrderBy(b => b.Authors!.Count == 1 ? 0 : 1);

            foreach (var b in matching)
            {
                var asin = b.AuthorAsins!.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
                if (string.IsNullOrWhiteSpace(asin))
                {
                    continue;
                }

                if (cachedNames.TryGetValue(asin, out var cachedName)
                    && !string.IsNullOrWhiteSpace(cachedName)
                    && !AuthorNameMatcher.SharesName(cachedName, name))
                {
                    continue;
                }

                return asin;
            }

            return null;
        }
    }
}
