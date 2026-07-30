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

using Listenarr.Application.Notifications.Progress;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Notifications
{
    [Trait("Category", "SearchActivityTracker")]
    public class SearchProgressReporterAutomaticTests
    {
        // Null hub context is supported by the reporter, so these exercise the
        // tracker-recording path without needing a SignalR mock.

        [Fact(DisplayName = "Automatic broadcast records the event in the tracker")]
        public async Task BroadcastAutomatic_RecordsToTracker()
        {
            var tracker = new SearchActivityTracker();
            var reporter = new SearchProgressReporter(null, NullLogger<SearchProgressReporter>.Instance, tracker);

            await reporter.BroadcastAutomaticAsync("Grabbed 1 for The Way of Kings", "grabbed", audiobookId: 51, asin: "B003ZWFO7E");

            var (current, recent) = tracker.Snapshot();
            Assert.NotNull(current);
            Assert.Equal("grabbed", current!.Stage);
            Assert.Equal(51, current.AudiobookId);
            Assert.Equal("B003ZWFO7E", current.Asin);
            Assert.Single(recent);
        }

        [Fact(DisplayName = "Transient automatic stage updates current but not the feed")]
        public async Task BroadcastAutomatic_Transient_NotInFeed()
        {
            var tracker = new SearchActivityTracker();
            var reporter = new SearchProgressReporter(null, NullLogger<SearchProgressReporter>.Instance, tracker);

            await reporter.BroadcastAutomaticAsync("Searching 13 indexers for X", "searching", addToFeed: false);

            var (current, recent) = tracker.Snapshot();
            Assert.Equal("searching", current!.Stage);
            Assert.Empty(recent);
        }

        [Fact(DisplayName = "Interactive broadcast does not touch the automatic tracker")]
        public async Task BroadcastInteractive_DoesNotRecord()
        {
            var tracker = new SearchActivityTracker();
            var reporter = new SearchProgressReporter(null, NullLogger<SearchProgressReporter>.Instance, tracker);

            await reporter.BroadcastAsync("Looking up ASIN…", "B000000000");

            var (current, recent) = tracker.Snapshot();
            Assert.Null(current);
            Assert.Empty(recent);
        }

        [Fact(DisplayName = "A stale 'searching' event is not reported as current")]
        public void Snapshot_StaleSearching_ReportsNoCurrent()
        {
            // Live case: a per-book "Search now" emitted 'searching' but its
            // code path never sent a terminal outcome — the sidebar showed
            // "Searching 3 indexers…" overnight. A searching event older than
            // any plausible real fan-out must not be reported as current.
            var tracker = new SearchActivityTracker();
            tracker.Record(new SearchActivityEvent(
                "Searching 3 indexers for 15th Affair James Patterson", "searching",
                null, null, DateTime.UtcNow.AddHours(-16)), addToFeed: false);

            var (current, _) = tracker.Snapshot();
            Assert.Null(current);
        }

        [Fact(DisplayName = "A fresh 'searching' event stays current; stale terminal stages are kept")]
        public void Snapshot_FreshSearchingAndOldOutcomes_Kept()
        {
            var tracker = new SearchActivityTracker();
            tracker.Record(new SearchActivityEvent(
                "Grabbed 1 release(s) for Old Book", "grabbed", 1, null,
                DateTime.UtcNow.AddHours(-16)));
            var (current, _) = tracker.Snapshot();
            // Terminal stages describe the PAST truthfully — age doesn't lie.
            Assert.NotNull(current);

            tracker.Record(new SearchActivityEvent(
                "Searching 3 indexers for X", "searching", null, null,
                DateTime.UtcNow.AddMinutes(-1)), addToFeed: false);
            (current, _) = tracker.Snapshot();
            Assert.NotNull(current);
            Assert.Equal("searching", current!.Stage);
        }
    }
}
