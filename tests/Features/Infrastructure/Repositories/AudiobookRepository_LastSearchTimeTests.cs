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
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Repositories
{
    // Real SQLite so the targeted-update SQL actually executes against a
    // relational schema (the InMemory provider used by the wider suite runs
    // the same detached-stub code path, but this pins the SQL translation).
    [Trait("Area", "Persistence")]
    [Trait("Name", "AudiobookRepository_LastSearchTimeTests")]
    public class AudiobookRepository_LastSearchTimeTests
    {
        private sealed class TestDb : IDisposable
        {
            private readonly SqliteConnection _connection;
            public ListenArrDbContext Db { get; }

            public TestDb()
            {
                _connection = new SqliteConnection("DataSource=:memory:");
                _connection.Open();
                Db = new ListenArrDbContext(
                    new DbContextOptionsBuilder<ListenArrDbContext>().UseSqlite(_connection).Options);
                Db.Database.EnsureCreated();
            }

            public void Dispose()
            {
                Db.Dispose();
                _connection.Dispose();
            }
        }

        [Fact]
        [Trait("Scenario", "StampsTimestamp_WithoutTouchingOtherColumns")]
        public async Task SetLastSearchTimeAsync_StampsTimestamp_WithoutTouchingOtherColumns()
        {
            // The live regression this guards: the automatic-search cycle held a
            // stale entity snapshot and re-saved the WHOLE row to bump
            // LastSearchTime, reverting a verification reset made minutes earlier.
            // The targeted write must leave every concurrently-changed column alone.
            using var ctx = new TestDb();
            var db = ctx.Db;
            var repository = new Listenarr.Infrastructure.Persistence.Repositories.AudiobookRepository(db);

            var book = new Audiobook
            {
                Title = "Dead Beat",
                VerificationStatus = VerificationStatus.AgentFlagged,
                VerificationTranscript = "stale transcript",
                Monitored = true
            };
            db.Audiobooks.Add(book);
            await db.SaveChangesAsync();

            // Simulate the concurrent change (e.g. a wrong-content verification
            // reset) landing in the database while the search cycle still holds
            // its stale tracked snapshot of the row. Raw SQL sidesteps the
            // tracked instance entirely, exactly like a parallel request scope.
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE Audiobooks SET VerificationStatus = {0}, VerificationTranscript = NULL WHERE Id = {1}",
                (int)VerificationStatus.Unverified, book.Id);

            var stamp = new DateTime(2026, 7, 5, 12, 0, 0, DateTimeKind.Utc);
            await repository.SetLastSearchTimeAsync(book.Id, stamp);

            var reloaded = await db.Audiobooks.AsNoTracking().FirstAsync(a => a.Id == book.Id);
            Assert.Equal(stamp, reloaded.LastSearchTime);
            // The concurrent reset survives — the timestamp write touched nothing else.
            Assert.Equal(VerificationStatus.Unverified, reloaded.VerificationStatus);
            Assert.Null(reloaded.VerificationTranscript);
        }

        [Fact]
        [Trait("Scenario", "TrackedSnapshotInSameContext_DoesNotThrow")]
        public async Task SetLastSearchTimeAsync_WithTrackedSnapshotInSameContext_DoesNotThrow()
        {
            // The sweep loads TRACKED entities in the same scope as the repository —
            // the stub attach must not trip the identity map on those.
            using var ctx = new TestDb();
            var db = ctx.Db;
            var repository = new Listenarr.Infrastructure.Persistence.Repositories.AudiobookRepository(db);

            var book = new Audiobook { Title = "Tracked Book", Monitored = true };
            db.Audiobooks.Add(book);
            await db.SaveChangesAsync();

            // Load a tracked instance (as GetMonitoredAudiobooksForSearchAsync does).
            var tracked = await db.Audiobooks.FirstAsync(a => a.Id == book.Id);
            Assert.NotNull(tracked);

            var stamp = new DateTime(2026, 7, 5, 13, 0, 0, DateTimeKind.Utc);
            await repository.SetLastSearchTimeAsync(book.Id, stamp);

            var reloaded = await db.Audiobooks.AsNoTracking().FirstAsync(a => a.Id == book.Id);
            Assert.Equal(stamp, reloaded.LastSearchTime);
        }
    }
}
