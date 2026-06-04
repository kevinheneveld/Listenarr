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
using Listenarr.Infrastructure.Persistence;
using Listenarr.Infrastructure.Persistence.Repositories;

namespace Listenarr.Tests.Features.Infrastructure.Repositories
{
    // Uses real SQLite (not the InMemory provider) so cache-read query translation is
    // actually exercised — the InMemory provider silently tolerates LINQ the SQLite provider
    // rejects at runtime.
    public class AudiobookRepository_SeriesCacheTests
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
        public async Task UpsertCachedSeriesForSlugAsync_OverwritesPoisonedSlugEntry_KeepingSlugKey()
        {
            using var ctx = new TestDb();
            var db = ctx.Db;
            var repository = new AudiobookRepository(db);

            // A prior wrong resolution cached the slug "After" -> the unrelated romance series,
            // including that series' description.
            await repository.UpsertCachedSeriesForSlugAsync("After", new SeriesCacheEntry
            {
                SeriesName = "After",
                SeriesAsin = "B07NYRLYKW",
                Region = "us",
                Description = "Ci sono momenti che ti cambiano la vita..."
            });

            // The user picks the correct series for the same slug (which has no description).
            await repository.UpsertCachedSeriesForSlugAsync("After", new SeriesCacheEntry
            {
                SeriesName = "A John Matherson Novel",
                SeriesAsin = "B015EXHCEE",
                Region = "us",
                Description = null
            });

            // Reading back by the slug now returns the chosen series — the poison is gone...
            var resolved = await repository.GetCachedSeriesByNameAsync("After", "us");
            Assert.NotNull(resolved);
            Assert.Equal("B015EXHCEE", resolved!.SeriesAsin);
            Assert.Equal("A John Matherson Novel", resolved.SeriesName);
            // ...and the previous series' description did not survive the re-point.
            Assert.Null(resolved.Description);

            // ...and it overwrote the existing row rather than inserting a duplicate slug entry.
            Assert.Equal(1, await db.SeriesCacheEntries.CountAsync(e => e.SeriesNameNormalized == "after"));
        }

        [Fact]
        public async Task UpsertCachedSeriesForSlugAsync_DoesNotDisturbADifferentSlugSharingTheSameAsin()
        {
            using var ctx = new TestDb();
            var db = ctx.Db;
            var repository = new AudiobookRepository(db);

            // The correct series page already has its own cache row, keyed by its real name.
            await repository.UpsertCachedSeriesForSlugAsync("A John Matherson Novel", new SeriesCacheEntry
            {
                SeriesName = "A John Matherson Novel",
                SeriesAsin = "B015EXHCEE",
                Region = "us"
            });

            // Picking the same ASIN for the wrong-named slug must not clobber the other row.
            await repository.UpsertCachedSeriesForSlugAsync("After", new SeriesCacheEntry
            {
                SeriesName = "A John Matherson Novel",
                SeriesAsin = "B015EXHCEE",
                Region = "us"
            });

            var bySlug = await repository.GetCachedSeriesByNameAsync("After", "us");
            var byName = await repository.GetCachedSeriesByNameAsync("A John Matherson Novel", "us");

            Assert.Equal("B015EXHCEE", bySlug!.SeriesAsin);
            Assert.Equal("B015EXHCEE", byName!.SeriesAsin);
            // Two distinct slug rows can legitimately share one ASIN.
            Assert.Equal(2, await db.SeriesCacheEntries.CountAsync(e => e.SeriesAsin == "B015EXHCEE"));
        }
    }
}
