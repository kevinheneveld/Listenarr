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

using System.Collections.Concurrent;

namespace Listenarr.Application.Audiobooks
{
    /// <summary>
    /// Serializes the "check existing audiobook + insert" critical section by
    /// ASIN so two concurrent <c>POST /library/add</c> requests for the same
    /// ASIN cannot both pass the dedup check and create duplicate rows.
    ///
    /// The lock is in-process only — sufficient for the single-instance
    /// deployment model. A DB-level UNIQUE constraint on <c>UPPER(Asin)</c> is
    /// a follow-up that will catch the multi-process case too (tracked in
    /// kevinheneveld/Listenarr#6).
    ///
    /// Different ASINs lock independently, so unrelated adds run in parallel.
    /// </summary>
    public static class AudiobookAddLockManager
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> _locks =
            new(StringComparer.Ordinal);

        /// <summary>
        /// Acquire the per-ASIN lock. Dispose the returned handle to release.
        /// Pass a null/empty/whitespace ASIN to get a no-op handle.
        /// </summary>
        public static async Task<IDisposable> AcquireAsync(string? asin, CancellationToken ct = default)
        {
            var normalized = (asin ?? string.Empty).Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(normalized))
            {
                return NoOpHandle.Instance;
            }

            var sem = _locks.GetOrAdd(normalized, static _ => new SemaphoreSlim(1, 1));
            await sem.WaitAsync(ct).ConfigureAwait(false);
            return new Releaser(sem);
        }

        /// <summary>
        /// Test-only escape hatch: drop all known locks. Production code should
        /// never call this — long-running entries are bounded by the number of
        /// distinct ASINs the server has ever processed and the
        /// <see cref="SemaphoreSlim"/> footprint is negligible.
        /// </summary>
        internal static void ResetForTesting()
        {
            foreach (var kv in _locks)
            {
                kv.Value.Dispose();
            }
            _locks.Clear();
        }

        private sealed class Releaser : IDisposable
        {
            private SemaphoreSlim? _sem;
            public Releaser(SemaphoreSlim sem) { _sem = sem; }
            public void Dispose()
            {
                var s = Interlocked.Exchange(ref _sem, null);
                s?.Release();
            }
        }

        private sealed class NoOpHandle : IDisposable
        {
            public static readonly NoOpHandle Instance = new();
            private NoOpHandle() { }
            public void Dispose() { }
        }
    }
}
