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
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Listenarr.Infrastructure.Persistence
{
    /// <summary>
    /// Applies SQLite PRAGMAs to every connection EF opens (including pooled connections
    /// created by the singleton <c>IDbContextFactory</c>). The critical one is
    /// <c>busy_timeout</c>: without it SQLite returns SQLITE_BUSY ("database is locked")
    /// immediately when another connection holds the writer lock. With it, a writer waits
    /// up to the configured interval before giving up, which absorbs the brief overlapping
    /// writes from the concurrent background services (download monitor, queue monitor,
    /// scan services, etc.).
    ///
    /// <c>busy_timeout</c> and <c>synchronous</c> are per-connection and non-persistent, so
    /// they must be re-applied on every open. <c>journal_mode=WAL</c> and
    /// <c>journal_size_limit</c> are database-global and persisted, but re-asserting them is
    /// a cheap idempotent no-op and guarantees correct state even on a freshly created DB.
    /// This replaces the never-wired-up <c>SqlitePragmaInitializer</c>.
    /// </summary>
    public sealed class SqliteConnectionPragmaInterceptor : DbConnectionInterceptor
    {
        /// <summary>How long (ms) a connection waits for the writer lock before SQLITE_BUSY.</summary>
        public const int BusyTimeoutMilliseconds = 5000;

        private const long JournalSizeLimitBytes = 6_144_000;

        private static readonly string PragmaSql =
            $"PRAGMA busy_timeout={BusyTimeoutMilliseconds};" +
            "PRAGMA journal_mode=WAL;" +
            "PRAGMA synchronous=NORMAL;" +
            $"PRAGMA journal_size_limit={JournalSizeLimitBytes};";

        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        {
            ApplyPragmas(connection);
            base.ConnectionOpened(connection, eventData);
        }

        public override async Task ConnectionOpenedAsync(
            DbConnection connection,
            ConnectionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            await ApplyPragmasAsync(connection, cancellationToken).ConfigureAwait(false);
            await base.ConnectionOpenedAsync(connection, eventData, cancellationToken).ConfigureAwait(false);
        }

        private static void ApplyPragmas(DbConnection connection)
        {
            // Only meaningful for SQLite; other providers (e.g. InMemory in tests) are skipped.
            if (connection is not SqliteConnection)
            {
                return;
            }

            using var cmd = connection.CreateCommand();
            cmd.CommandText = PragmaSql;
            cmd.ExecuteNonQuery();
        }

        private static async Task ApplyPragmasAsync(DbConnection connection, CancellationToken cancellationToken)
        {
            if (connection is not SqliteConnection)
            {
                return;
            }

            await using var cmd = connection.CreateCommand();
            cmd.CommandText = PragmaSql;
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
