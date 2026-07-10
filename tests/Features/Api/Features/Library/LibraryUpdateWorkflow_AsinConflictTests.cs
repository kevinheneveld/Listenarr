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
using System.Text.Json;
using Listenarr.Api.Features.Library;
using Listenarr.Infrastructure.Persistence.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Api.Features.Library
{
    /// <summary>
    /// The unique-ASIN backstop is real relational index behavior (see
    /// AsinUniqueIndexTests) — this needs a real SQLite database, not the
    /// InMemory provider the rest of the LibraryController tests use, so
    /// LibraryUpdateWorkflow is constructed directly here rather than going
    /// through the BaseTests DI harness.
    /// </summary>
    [Trait("Area", "LibraryApi")]
    [Trait("Name", "LibraryUpdateWorkflow_AsinConflictTests")]
    public class LibraryUpdateWorkflow_AsinConflictTests
    {
        private static JsonElement ToJson(object? value) => JsonSerializer.SerializeToElement(value);

        private static (SqliteConnection Connection, ListenArrDbContext Context, LibraryUpdateWorkflow Workflow) CreateWorkflow()
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            connection.Open();

            var options = new DbContextOptionsBuilder<ListenArrDbContext>()
                .UseSqlite(connection, sqlite =>
                    sqlite.MigrationsAssembly(typeof(ListenArrDbContext).Assembly.GetName().Name))
                .Options;

            var context = new ListenArrDbContext(options);
            context.Database.Migrate();

            var repo = new AudiobookRepository(context);
            var scopeFactory = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
            var workflow = new LibraryUpdateWorkflow(repo, scopeFactory, NullLogger<LibraryUpdateWorkflow>.Instance);
            return (connection, context, workflow);
        }

        [Fact]
        [Trait("Method", "UpdateAsync")]
        [Trait("Scenario", "AsinCollision_ReturnsConflictWithWinnerDetails")]
        public async Task UpdateAsync_AsinCollidesWithAnotherRow_ReturnsEnrichedConflict()
        {
            var (connection, context, workflow) = CreateWorkflow();
            using var _conn = connection;
            using var _ctx = context;

            var repo = new AudiobookRepository(context);
            await repo.EnsureAsinUniqueIndexAsync();

            var existing = await repo.AddAsync(new Audiobook
            {
                Title = "The Shattering Peace",
                Authors = new List<string> { "John Scalzi" },
                Asin = "B0DZTMXQZ7",
            });
            var mislabeled = await repo.AddAsync(new Audiobook { Title = "The Ring", Authors = new List<string> { "Piers Anthony" } });

            var result = await workflow.UpdateAsync(mislabeled.Id, new Audiobook { Asin = "B0DZTMXQZ7" });

            var conflictResult = Assert.IsType<ConflictObjectResult>(result);
            var payload = ToJson(conflictResult.Value);
            Assert.Equal("asin_conflict", payload.GetProperty("code").GetString());
            var conflict = payload.GetProperty("conflict");
            Assert.Equal(existing.Id, conflict.GetProperty("audiobookId").GetInt32());
            Assert.Equal("The Shattering Peace", conflict.GetProperty("title").GetString());
            Assert.Equal("B0DZTMXQZ7", conflict.GetProperty("asin").GetString());

            // The rejected write must not have stuck around in the database
            // (the tracked in-memory entity keeps its mutated value after a
            // failed SaveChangesAsync — that's normal EF behavior and harmless
            // in production, where each request gets a fresh scoped
            // DbContext; clear the tracker here to check what's actually
            // persisted).
            context.ChangeTracker.Clear();
            var reloadedMislabeled = await repo.GetByIdAsync(mislabeled.Id);
            Assert.NotEqual("B0DZTMXQZ7", reloadedMislabeled!.Asin);
        }
    }
}
