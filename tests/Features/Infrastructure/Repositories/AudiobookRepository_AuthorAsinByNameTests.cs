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
using Listenarr.Tests.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Listenarr.Tests.Features.Infrastructure.Repositories
{
    /// <summary>
    /// The author page resolves a bare name through the library's stored author ASINs.
    /// A relabelled book carries the OLD author's ASIN next to the NEW author's name, so
    /// that lookup must distrust an ASIN the author cache attributes to someone else.
    /// </summary>
    [Trait("Name", "AudiobookRepository_AuthorAsinByNameTests")]
    [Trait("Category", "Library")]
    public class AudiobookRepository_AuthorAsinByNameTests : BaseTests
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
        public async Task GetAuthorAsinByName_StaleAsinContradictedByCache_ReturnsNull()
        {
            using var ctx = new TestDb();
            var repository = new AudiobookRepository(ctx.Db);
            await repository.AddAsync(new Audiobook
            {
                Title = "Cross Fire",
                Authors = new List<string> { "Fonda Lee" },
                AuthorAsins = new List<string> { "B000APZGGS" }
            });
            await repository.UpsertCachedAuthorAsync(new AuthorCacheEntry
            {
                AuthorName = "James Patterson",
                AuthorAsin = "B000APZGGS",
                Region = "us"
            });

            Assert.Null(await repository.GetAuthorAsinByNameAsync("Fonda Lee"));
        }

        [Fact]
        public async Task GetAuthorAsinByName_UncachedAsin_KeepsLegacyBehaviour()
        {
            using var ctx = new TestDb();
            var repository = new AudiobookRepository(ctx.Db);
            await repository.AddAsync(new Audiobook
            {
                Title = "Cross Fire",
                Authors = new List<string> { "Fonda Lee" },
                AuthorAsins = new List<string> { "B0FONDA001" }
            });

            Assert.Equal("B0FONDA001", await repository.GetAuthorAsinByNameAsync("Fonda Lee"));
        }

        [Fact]
        public async Task GetAuthorAsinByName_PrefersSingleAuthorBookAndSkipsContradictedOne()
        {
            using var ctx = new TestDb();
            var repository = new AudiobookRepository(ctx.Db);
            // Multi-author book listed first with a contradicted ASIN[0].
            await repository.AddAsync(new Audiobook
            {
                Title = "Anthology",
                Authors = new List<string> { "Fonda Lee", "Someone Else" },
                AuthorAsins = new List<string> { "B000APZGGS", "B0OTHER001" }
            });
            await repository.AddAsync(new Audiobook
            {
                Title = "Jade City",
                Authors = new List<string> { "Fonda Lee" },
                AuthorAsins = new List<string> { "B0FONDA001" }
            });
            await repository.UpsertCachedAuthorAsync(new AuthorCacheEntry
            {
                AuthorName = "James Patterson",
                AuthorAsin = "B000APZGGS",
                Region = "us"
            });
            await repository.UpsertCachedAuthorAsync(new AuthorCacheEntry
            {
                AuthorName = "Fonda Lee",
                AuthorAsin = "B0FONDA001",
                Region = "us"
            });

            Assert.Equal("B0FONDA001", await repository.GetAuthorAsinByNameAsync("Fonda Lee"));
        }
    }
}
