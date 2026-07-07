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

using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

namespace Listenarr.Api.Features.Library
{
    /// <summary>
    /// Short-lived memoization for the dashboard's heavy full-library
    /// aggregates (series health, dashboard stats, duplicates, music
    /// candidates, and the queue-status endpoint's series-backfill progress —
    /// the latter polled every 30 seconds). Each key caches for five minutes.
    ///
    /// Invalidation: there is no single "library changed" choke point in the
    /// codebase (scan/move/import broadcasts are scattered across processors),
    /// so background mutations simply age out — up to five minutes of
    /// staleness on dashboard aggregates is an accepted tradeoff. The
    /// user-facing mutation workflows whose UI refetches immediately after
    /// acting (series backfill-now, duplicate merge, not-audiobook purge)
    /// call <see cref="InvalidateAll"/> so their refresh can never read a
    /// stale snapshot of the thing the user just changed.
    /// </summary>
    public sealed class DashboardAggregateCache
    {
        private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);

        private readonly IMemoryCache _cache;
        private CancellationTokenSource _epoch = new();

        public DashboardAggregateCache(IMemoryCache cache)
        {
            _cache = cache;
        }

        public async Task<T> GetOrCreateAsync<T>(string key, Func<Task<T>> factory)
        {
            var cacheKey = $"dashboard-aggregate:{key}";
            if (_cache.TryGetValue(cacheKey, out T? hit) && hit is not null)
            {
                return hit;
            }

            var value = await factory();
            var options = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = Ttl,
            };
            // The epoch token lets InvalidateAll evict every aggregate at once
            // without tracking individual keys.
            options.ExpirationTokens.Add(new CancellationChangeToken(_epoch.Token));
            _cache.Set(cacheKey, value, options);
            return value;
        }

        /// <summary>Evict every cached aggregate (cheap; new epoch token).</summary>
        public void InvalidateAll()
        {
            var old = Interlocked.Exchange(ref _epoch, new CancellationTokenSource());
            old.Cancel();
            old.Dispose();
        }
    }
}
