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
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Listenarr.Domain.Models;
using Listenarr.Domain.Models.Enumerations;
using Listenarr.Infrastructure.Persistence;
using Listenarr.Infrastructure.Persistence.Repositories;

namespace Listenarr.Tests.Features.Infrastructure.Repositories
{
    // Real SQLite so ExecuteUpdateAsync's SQL translation is actually exercised.
    public class AudiobookRepository_LastSearchTimeTests
    {
        private sealed class TestDb : System.IDisposable
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
        public async Task SetLastSearchTimeAsync_StampsTimestamp_WithoutTouchingOtherColumns()
        {
            // The live regression this guards: the automatic-search cycle held a
            // stale entity snapshot and re-saved the WHOLE row to bump
            // LastSearchTime, reverting a verification reset made minutes earlier.
            // The targeted write must leave every concurrently-changed column alone.
            using var ctx = new TestDb();
            var db = ctx.Db;
            var repository = new AudiobookRepository(db);

            var book = new Audiobook
            {
                Title = "Dead Beat",
                VerificationStatus = VerificationStatus.AgentFlagged,
                VerificationTranscript = "stale transcript",
                Monitored = true
            };
            db.Audiobooks.Add(book);
            await db.SaveChangesAsync();

            // Simulate the concurrent change (e.g. a not-audiobook verification
            // reset) landing in the database while the search cycle still holds
            // its stale tracked snapshot of the row.
            await db.Audiobooks
                .Where(a => a.Id == book.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(a => a.VerificationStatus, VerificationStatus.Unverified)
                    .SetProperty(a => a.VerificationTranscript, (string?)null));

            var stamp = new System.DateTime(2026, 6, 11, 18, 0, 0, System.DateTimeKind.Utc);
            await repository.SetLastSearchTimeAsync(book.Id, stamp);

            var reloaded = await db.Audiobooks.AsNoTracking().FirstAsync(a => a.Id == book.Id);
            Assert.Equal(stamp, reloaded.LastSearchTime);
            // The concurrent reset survives — the timestamp write touched nothing else.
            Assert.Equal(VerificationStatus.Unverified, reloaded.VerificationStatus);
            Assert.Null(reloaded.VerificationTranscript);
        }
    }
}
