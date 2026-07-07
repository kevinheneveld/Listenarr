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
using Listenarr.Infrastructure.Persistence.Repositories;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Persistence
{
    /// <summary>
    /// The unique-ASIN backstop (upstream issue #6) is deliberately a startup
    /// ensure step, not an EF migration — CREATE UNIQUE INDEX fails outright
    /// on databases already holding duplicates, and a failed migration blocks
    /// every later migration on that install. These tests run against real
    /// SQLite because the whole point is relational index behavior.
    /// </summary>
    [Trait("Area", "Persistence")]
    [Trait("Name", "AsinUniqueIndexTests")]
    public class AsinUniqueIndexTests
    {
        private static (SqliteConnection Connection, ListenArrDbContext Context) CreateMigratedSqliteContext()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            connection.Open();

            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(connection, sqlite =>
                    sqlite.MigrationsAssembly(typeof(ListenArrDbContext).Assembly.GetName().Name))
                .Options;

            var context = new ListenArrDbContext(options);
            context.Database.Migrate();
            return (connection, context);
        }

        private static bool IndexExists(SqliteConnection connection)
        {
            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='IX_Audiobooks_Asin_Unique'";
            return Convert.ToInt32(command.ExecuteScalar()) > 0;
        }

        [Fact]
        [Trait("Scenario", "CleanDatabase_CreatesIndex_Idempotently")]
        public async Task EnsureAsinUniqueIndex_CleanDatabase_CreatesIndex_Idempotently()
        {
            var (connection, context) = CreateMigratedSqliteContext();
            using var _conn = connection;
            using var _ctx = context;
            var repo = new AudiobookRepository(context);

            var first = await repo.EnsureAsinUniqueIndexAsync();
            Assert.Equal(AsinIndexOutcome.Ensured, first.Outcome);
            Assert.True(IndexExists(connection));

            // Idempotent: a second boot must not fail.
            var second = await repo.EnsureAsinUniqueIndexAsync();
            Assert.Equal(AsinIndexOutcome.Ensured, second.Outcome);
        }

        [Fact]
        [Trait("Scenario", "DirtyDatabase_SkipsWithCount_NoIndex")]
        public async Task EnsureAsinUniqueIndex_ExistingDuplicates_SkipsAndReportsCount()
        {
            var (connection, context) = CreateMigratedSqliteContext();
            using var _conn = connection;
            using var _ctx = context;

            context.Audiobooks.Add(new Audiobook { Title = "Copy A", Asin = "B000DUPE01" });
            context.Audiobooks.Add(new Audiobook { Title = "Copy B", Asin = "B000DUPE01" });
            context.Audiobooks.Add(new Audiobook { Title = "Fine", Asin = "B000CLEAN1" });
            await context.SaveChangesAsync();

            var repo = new AudiobookRepository(context);
            var result = await repo.EnsureAsinUniqueIndexAsync();

            Assert.Equal(AsinIndexOutcome.SkippedDuplicatesExist, result.Outcome);
            Assert.Equal(1, result.DuplicateAsinGroups);
            Assert.False(IndexExists(connection));
        }

        [Fact]
        [Trait("Scenario", "IndexPresent_RaceLoserGetsMappedException")]
        public async Task EnsureAsinUniqueIndex_BackstopFires_AsUniqueConstraintViolation()
        {
            var (connection, context) = CreateMigratedSqliteContext();
            using var _conn = connection;
            using var _ctx = context;
            var repo = new AudiobookRepository(context);
            await repo.EnsureAsinUniqueIndexAsync();

            context.Audiobooks.Add(new Audiobook { Title = "Winner", Asin = "B000RACE01" });
            await context.SaveChangesAsync();

            context.Audiobooks.Add(new Audiobook { Title = "Loser", Asin = "B000RACE01" });
            await Assert.ThrowsAsync<UniqueConstraintViolationException>(
                () => context.SaveChangesAsync());

            // NULL/empty ASINs stay unconstrained (partial index).
            context.ChangeTracker.Clear();
            context.Audiobooks.Add(new Audiobook { Title = "Wishlist 1", Asin = null });
            context.Audiobooks.Add(new Audiobook { Title = "Wishlist 2", Asin = null });
            await context.SaveChangesAsync();
        }
    }
}
