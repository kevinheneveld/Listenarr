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

namespace Listenarr.Tests.Features.Application.Common
{
    [Trait("Name", "RepeatedLogSuppressorTests")]
    [Trait("Category", "Logging")]
    public class RepeatedLogSuppressorTests : BaseTests
    {
        private sealed class MutableTimeProvider : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = new(2026, 9, 13, 8, 0, 0, TimeSpan.Zero);
            public override DateTimeOffset GetUtcNow() => Now;
        }

        [Fact]
        public void ShouldLog_FirstOccurrenceAllowed_RepeatsInsideWindowSuppressed()
        {
            var clock = new MutableTimeProvider();
            var suppressor = new RepeatedLogSuppressor(clock);

            Assert.True(suppressor.ShouldLog("k"));
            Assert.False(suppressor.ShouldLog("k"));
            clock.Now += TimeSpan.FromHours(1);
            Assert.False(suppressor.ShouldLog("k"));
        }

        [Fact]
        public void ShouldLog_AfterWindow_FiresAgain()
        {
            var clock = new MutableTimeProvider();
            var suppressor = new RepeatedLogSuppressor(clock);
            Assert.True(suppressor.ShouldLog("k"));

            clock.Now += RepeatedLogSuppressor.DefaultWindow;

            Assert.True(suppressor.ShouldLog("k"));
            Assert.False(suppressor.ShouldLog("k"));
        }

        [Fact]
        public void ShouldLog_KeysAreIndependent_AndCustomWindowHonoured()
        {
            var clock = new MutableTimeProvider();
            var suppressor = new RepeatedLogSuppressor(clock);

            Assert.True(suppressor.ShouldLog("a", TimeSpan.FromMinutes(5)));
            Assert.True(suppressor.ShouldLog("b", TimeSpan.FromMinutes(5)));
            clock.Now += TimeSpan.FromMinutes(6);
            Assert.True(suppressor.ShouldLog("a", TimeSpan.FromMinutes(5)));
        }
    }
}
