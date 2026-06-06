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
using Listenarr.Infrastructure.Extensions;
using Listenarr.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Listenarr.Tests.Features.Infrastructure.Persistence
{
    public class SqliteConnectionPragmaInterceptorTests
    {
        // Verifies the PRAGMA interceptor is actually wired into the production DI path:
        // a context produced by the singleton IDbContextFactory must report the configured
        // busy_timeout on a connection EF opened. Asserting via the factory (not a hand-built
        // DbContextOptions) is the point — it exercises the AddInterceptors registration in
        // AddListenarrInfrastructure rather than the interceptor class in isolation.
        [Fact]
        public async Task FactoryCreatedContext_HasConfiguredBusyTimeout()
        {
            // Own subdir per test so the whole thing (db + the -wal/-shm sidecars WAL creates)
            // can be deleted wholesale.
            var dbDir = Path.Combine(
                Path.GetTempPath(),
                "listenarr-tests",
                $"busy-timeout-{System.Guid.NewGuid():N}");
            Directory.CreateDirectory(dbDir);
            var dbPath = Path.Combine(dbDir, "listenarr.db");

            try
            {
                var services = new ServiceCollection();
                services.AddListenarrInfrastructure(options =>
                    options.UseSqlite($"Data Source={dbPath}")
                        // Build a dedicated EF internal service provider for this context instead
                        // of sharing EF Core's process-global ServiceProviderCache. Sibling SQLite
                        // tests construct ListenArrDbContext with UseSqlite and no interceptor; under
                        // parallel execution a cached interceptor-less provider can otherwise be
                        // handed to this factory's context, so the interceptor never fires and the
                        // read sees 0. Production registers exactly one (with-interceptor) options
                        // shape, so the cache is never contaminated there — this is test-only.
                        .EnableServiceProviderCaching(false));

                using var sp = services.BuildServiceProvider(validateScopes: true);

                var factory = sp.GetRequiredService<IDbContextFactory<ListenArrDbContext>>();
                await using var context = factory.CreateDbContext();

                // Read busy_timeout through a single EF query: EF opens the connection (firing
                // the interceptor that applies the PRAGMA) and runs the SELECT on that same
                // connection, so the value read is the one the interceptor just set — the exact
                // production path, with no window for EF/pool to hand back a different connection.
                var result = await context.Database
                    .SqlQueryRaw<long>("SELECT timeout AS \"Value\" FROM pragma_busy_timeout()")
                    .SingleAsync();

                Assert.Equal(SqliteConnectionPragmaInterceptor.BusyTimeoutMilliseconds, result);
            }
            finally
            {
                if (Directory.Exists(dbDir))
                {
                    Directory.Delete(dbDir, recursive: true);
                }
            }
        }
    }
}
