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
using Listenarr.Domain.Audiobooks.Enumerations;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Repositories
{
    public partial class AudiobookRepository
    {
        public async Task<int> CountVerifiedSinceAsync(DateTime sinceUtc, CancellationToken ct = default)
        {
            // Targeted COUNT: the dashboard polls this every ~30s while visible,
            // so it must not materialize entities.
            return await _db.Audiobooks
                .AsNoTracking()
                .CountAsync(a => a.VerifiedAt != null && a.VerifiedAt >= sinceUtc, ct);
        }

        public async Task<List<int>> GetIdsVerifiedSinceAsync(DateTime sinceUtc, IReadOnlyCollection<int> candidateIds, CancellationToken ct = default)
        {
            if (candidateIds.Count == 0) return new List<int>();

            return await _db.Audiobooks
                .AsNoTracking()
                .Where(a => candidateIds.Contains(a.Id) && a.VerifiedAt != null && a.VerifiedAt >= sinceUtc)
                .Select(a => a.Id)
                .ToListAsync(ct);
        }

        public async Task<List<int>> GetAiReviewCandidateIdsAsync(int afterId, int limit, CancellationToken ct = default)
        {
            // "aiReview" is the camelCase property the detail serializer writes
            // when a review was folded in — its absence marks a verdict the
            // model has not seen. A LIKE on the JSON column is crude but the
            // population is a few hundred rows and this runs on demand only.
            return await _db.Audiobooks
                .AsNoTracking()
                .Where(a => a.Id > afterId
                    && (a.VerificationStatus == VerificationStatus.AgentFlagged
                        || a.VerificationStatus == VerificationStatus.AgentUnverifiable)
                    && a.VerificationTranscript != null
                    && a.VerificationDetailJson != null
                    && !a.VerificationDetailJson.Contains("\"aiReview\""))
                .OrderBy(a => a.Id)
                .Take(limit)
                .Select(a => a.Id)
                .ToListAsync(ct);
        }
    }
}
