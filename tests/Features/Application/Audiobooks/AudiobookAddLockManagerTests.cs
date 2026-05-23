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

using Xunit;
using Listenarr.Application.Audiobooks;

namespace Listenarr.Tests.Features.Application.Audiobooks
{
    /// <summary>
    /// Behavior tests for the per-ASIN add lock that prevents the race
    /// condition where two concurrent /library/add requests for the same ASIN
    /// both pass the dedup check and create duplicate rows (issue #6).
    /// </summary>
    public class AudiobookAddLockManagerTests
    {
        public AudiobookAddLockManagerTests()
        {
            AudiobookAddLockManager.ResetForTesting();
        }

        [Fact]
        public async Task SameAsin_SerializesConcurrentAcquirers()
        {
            // Two tasks "enter" the critical section for the same ASIN. The
            // second must wait until the first releases.
            const string asin = "B00TESTRACE";

            var firstEntered = new TaskCompletionSource<bool>();
            var releaseFirst = new TaskCompletionSource<bool>();
            var secondEnteredBeforeRelease = false;

            var first = Task.Run(async () =>
            {
                using var handle = await AudiobookAddLockManager.AcquireAsync(asin);
                firstEntered.SetResult(true);
                await releaseFirst.Task;
            });

            await firstEntered.Task;

            var second = Task.Run(async () =>
            {
                using var handle = await AudiobookAddLockManager.AcquireAsync(asin);
                // If the lock failed to serialize, this line would run while
                // releaseFirst is still unset and the flag would flip true.
                secondEnteredBeforeRelease = !releaseFirst.Task.IsCompleted;
            });

            // Give the second acquirer time to attempt; it must block.
            await Task.Delay(50);
            Assert.False(second.IsCompleted, "Second acquirer should be blocked while first holds the lock");

            releaseFirst.SetResult(true);
            await Task.WhenAll(first, second);

            Assert.False(secondEnteredBeforeRelease,
                "Second acquirer entered the critical section before the first released — lock did not serialize");
        }

        [Fact]
        public async Task DifferentAsins_RunIndependently()
        {
            // Acquiring the lock for ASIN-A must NOT block acquirers for ASIN-B.
            const string asinA = "B00TESTAAAA";
            const string asinB = "B00TESTBBBB";

            using var handleA = await AudiobookAddLockManager.AcquireAsync(asinA);

            // Try to acquire B with a short timeout — if it blocks we've
            // created accidental cross-ASIN contention.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var handleB = await AudiobookAddLockManager.AcquireAsync(asinB, cts.Token);
            handleB.Dispose();
        }

        [Fact]
        public async Task NullOrWhitespaceAsin_ReturnsNoOpHandle_NoSerialization()
        {
            // Add paths without an ASIN should not serialize on each other —
            // there's no shared key to dedup against. The handle is still
            // disposable for the using-statement contract.
            using var h1 = await AudiobookAddLockManager.AcquireAsync(null);
            using var h2 = await AudiobookAddLockManager.AcquireAsync(string.Empty);
            using var h3 = await AudiobookAddLockManager.AcquireAsync("   ");

            // All three handles must be acquirable simultaneously; if any were
            // a real semaphore we'd deadlock or block here.
            Assert.NotNull(h1);
            Assert.NotNull(h2);
            Assert.NotNull(h3);
        }

        [Fact]
        public async Task AsinIsCaseInsensitive()
        {
            // The dedup check normalizes ASIN to upper-case; the lock must do
            // the same so "b00TestCase" and "B00TESTCASE" can't both win.
            const string lower = "b00testcase";
            const string upper = "B00TESTCASE";

            var firstEntered = new TaskCompletionSource<bool>();
            var releaseFirst = new TaskCompletionSource<bool>();
            var secondEntered = false;

            var first = Task.Run(async () =>
            {
                using var handle = await AudiobookAddLockManager.AcquireAsync(lower);
                firstEntered.SetResult(true);
                await releaseFirst.Task;
            });

            await firstEntered.Task;

            var second = Task.Run(async () =>
            {
                using var handle = await AudiobookAddLockManager.AcquireAsync(upper);
                secondEntered = true;
            });

            await Task.Delay(50);
            Assert.False(second.IsCompleted, "Second acquirer (upper-case) should block while first (lower-case) holds the lock");
            Assert.False(secondEntered);

            releaseFirst.SetResult(true);
            await Task.WhenAll(first, second);

            Assert.True(secondEntered);
        }

        [Fact]
        public async Task ReleasingTwice_IsSafe()
        {
            // Defensive: disposing the handle twice (via explicit Dispose +
            // using) must not over-release the semaphore. If it did, a third
            // acquirer would also get in immediately, breaking serialization.
            const string asin = "B00TESTREL";

            var handle = await AudiobookAddLockManager.AcquireAsync(asin);
            handle.Dispose();
            handle.Dispose();

            // After the (single) release, the next acquire must succeed
            // immediately. After a second release the count would be 2 — but
            // there's no way to test that directly except by trying a third
            // acquire and confirming it still blocks behind a held lock.
            using var h2 = await AudiobookAddLockManager.AcquireAsync(asin);

            var h3Task = Task.Run(async () =>
            {
                using var h3 = await AudiobookAddLockManager.AcquireAsync(asin);
            });

            await Task.Delay(50);
            Assert.False(h3Task.IsCompleted,
                "Third acquirer must block — over-release would let it pass through immediately");
        }
    }
}
