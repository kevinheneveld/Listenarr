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

using Listenarr.Tests.Common;
using Microsoft.Data.Sqlite;

namespace Listenarr.Tests.Features.Infrastructure.Library.Moving
{
    /// <summary>
    /// A heartbeat that cannot write because another writer holds the SQLite lock must not
    /// cancel the job it protects; only the ownership deadline decides that.
    /// </summary>
    [Trait("Name", "MoveBackgroundService_HeartbeatContentionTests")]
    [Trait("Category", "BackgroundWorkers")]
    public sealed class MoveBackgroundService_HeartbeatContentionTests : BaseTests
    {
        [Fact]
        public void IsTransientDatabaseContention_RecognizesSqliteBusyAndLocked_AtAnyDepth()
        {
            Assert.True(MoveBackgroundService.IsTransientDatabaseContention(new SqliteException("database is locked", 5)));
            Assert.True(MoveBackgroundService.IsTransientDatabaseContention(new SqliteException("locked", 6)));
            Assert.True(MoveBackgroundService.IsTransientDatabaseContention(
                new PersistenceException("wrapped", new InvalidOperationException("ef", new SqliteException("database is locked", 5)))));
        }

        [Fact]
        public void IsTransientDatabaseContention_IgnoresOtherFailures()
        {
            Assert.False(MoveBackgroundService.IsTransientDatabaseContention(new SqliteException("constraint", 19)));
            Assert.False(MoveBackgroundService.IsTransientDatabaseContention(new PersistenceException("wrapped", new InvalidOperationException("boom"))));
            Assert.False(MoveBackgroundService.IsTransientDatabaseContention(new IOException("disk")));
        }
    }
}
