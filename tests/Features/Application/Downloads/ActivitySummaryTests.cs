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
using Listenarr.Application.Downloads;
using Listenarr.Domain.Models;
using Xunit;

namespace Listenarr.Tests.Features.Application.Downloads
{
    public class ActivitySummaryTests
    {
        private static readonly DateTime Now = new(2026, 6, 7, 12, 0, 0, DateTimeKind.Utc);

        private static Download Make(
            DownloadStatus status,
            string title = "Book",
            int? audiobookId = null,
            DateTime? startedAt = null,
            DateTime? completedAt = null,
            string? error = null,
            string? blockReason = null,
            string clientId = "qbit")
            => new()
            {
                Id = Guid.NewGuid().ToString(),
                Title = title,
                AudiobookId = audiobookId,
                Status = status,
                StartedAt = startedAt ?? Now.AddHours(-1),
                CompletedAt = completedAt,
                ErrorMessage = error,
                ImportBlockReason = blockReason,
                DownloadClientId = clientId,
            };

        [Theory]
        [InlineData(DownloadStatus.Queued, ActivityCategory.InProgress)]
        [InlineData(DownloadStatus.Downloading, ActivityCategory.InProgress)]
        [InlineData(DownloadStatus.Paused, ActivityCategory.InProgress)]
        [InlineData(DownloadStatus.Processing, ActivityCategory.InProgress)]
        [InlineData(DownloadStatus.ImportPending, ActivityCategory.InProgress)]
        [InlineData(DownloadStatus.ImportBlocked, ActivityCategory.Blocked)]
        [InlineData(DownloadStatus.Completed, ActivityCategory.Imported)]
        [InlineData(DownloadStatus.Ready, ActivityCategory.Imported)]
        [InlineData(DownloadStatus.Moved, ActivityCategory.Imported)]
        public void Classify_MapsStatusesToCategories(DownloadStatus status, ActivityCategory expected)
        {
            Assert.Equal(expected, ActivitySummary.Classify(Make(status)));
        }

        [Fact]
        public void Classify_FailedWithStallMessage_IsStalled_OtherwiseFailed()
        {
            var stalled = Make(DownloadStatus.Failed, error: "Reaped by stall timer: no download progress");
            var failed = Make(DownloadStatus.Failed, error: "qBittorrent state: missingFiles");

            Assert.Equal(ActivityCategory.Stalled, ActivitySummary.Classify(stalled));
            Assert.Equal(ActivityCategory.Failed, ActivitySummary.Classify(failed));
        }

        [Fact]
        public void Build_CollapsesAttemptsPerBook_LatestAttemptWins()
        {
            // Same book (audiobookId 1), three attempts; newest is currently downloading.
            var downloads = new[]
            {
                Make(DownloadStatus.Failed, audiobookId: 1, startedAt: Now.AddDays(-2), error: "Reaped by stall timer: no progress"),
                Make(DownloadStatus.Failed, audiobookId: 1, startedAt: Now.AddDays(-1), error: "Reaped by stall timer: no progress"),
                Make(DownloadStatus.Downloading, audiobookId: 1, startedAt: Now.AddMinutes(-10)),
            };

            var result = ActivitySummary.Build(downloads, Now);

            // One collapsed row, categorized by the latest attempt, with the full attempt count.
            var item = Assert.Single(result.Items);
            Assert.Equal(ActivityCategory.InProgress, item.Category);
            Assert.Equal(3, item.AttemptCount);

            // Counts are not double-counted: the book is in-progress only, not also stalled.
            Assert.Equal(1, result.Summary.InProgress);
            Assert.Equal(0, result.Summary.Stalled);
        }

        [Fact]
        public void Build_Collapse_ActiveAttemptWins_OverMoreRecentlyFinishedFailure()
        {
            // A book whose newest *finished* attempt failed, but which also has an attempt still
            // downloading. The active attempt must represent the book so it stays in the default view.
            var downloads = new[]
            {
                Make(DownloadStatus.Failed, audiobookId: 1, completedAt: Now.AddHours(-1), error: "disk full"),
                Make(DownloadStatus.Downloading, audiobookId: 1, startedAt: Now.AddDays(-1)),
            };

            var result = ActivitySummary.Build(downloads, Now);

            var item = Assert.Single(result.Items);
            Assert.Equal(ActivityCategory.InProgress, item.Category);
            Assert.Equal(1, result.Summary.InProgress);
            Assert.Equal(0, result.Summary.Failed);
        }

        [Fact]
        public void Build_DefaultView_ExcludesBlocked_ButChipCountStaysAllTime()
        {
            var downloads = new[]
            {
                Make(DownloadStatus.Downloading, audiobookId: 1, startedAt: Now.AddMinutes(-5)),
                Make(DownloadStatus.ImportBlocked, audiobookId: 2, startedAt: Now.AddHours(-2)), // recent block
                Make(DownloadStatus.ImportBlocked, audiobookId: 3, startedAt: Now.AddDays(-30)), // old backlog
            };

            var result = ActivitySummary.Build(downloads, Now);

            // Default list shows only the in-progress item; blocked (recent or not) drops off.
            var item = Assert.Single(result.Items);
            Assert.Equal(1, item.AudiobookId);
            // ...but the chip count reflects the full standing blocked backlog.
            Assert.Equal(2, result.Summary.Blocked);
        }

        [Fact]
        public void Build_BlockedFilter_ReturnsFullBacklog_NotWindowed()
        {
            var downloads = new[]
            {
                Make(DownloadStatus.ImportBlocked, audiobookId: 1, startedAt: Now.AddHours(-2)),
                Make(DownloadStatus.ImportBlocked, audiobookId: 2, startedAt: Now.AddDays(-30)),
            };

            var result = ActivitySummary.Build(downloads, Now, filter: ActivityCategory.Blocked);

            Assert.Equal(2, result.Items.Count); // drilling into Blocked shows the whole backlog
        }

        [Fact]
        public void Build_DefaultView_ShowsOnlyInProgress_DropsBlockedAndTerminal()
        {
            var downloads = new[]
            {
                Make(DownloadStatus.Downloading, title: "Active", audiobookId: 1),
                Make(DownloadStatus.ImportBlocked, title: "Blocked", audiobookId: 2),
                Make(DownloadStatus.Moved, title: "Imported", audiobookId: 3, completedAt: Now.AddHours(-1)),
                Make(DownloadStatus.Failed, title: "Failed", audiobookId: 4, completedAt: Now.AddHours(-1), error: "boom"),
            };

            var result = ActivitySummary.Build(downloads, Now);

            var titles = result.Items.Select(i => i.Title).ToHashSet();
            Assert.Contains("Active", titles);
            Assert.DoesNotContain("Blocked", titles);
            Assert.DoesNotContain("Imported", titles);
            Assert.DoesNotContain("Failed", titles);
        }

        [Fact]
        public void Build_CategoryFilter_ReturnsOnlyThatCategory_WindowedForTerminal()
        {
            var downloads = new[]
            {
                Make(DownloadStatus.Failed, title: "RecentFail", audiobookId: 1, completedAt: Now.AddHours(-2), error: "disk full"),
                Make(DownloadStatus.Failed, title: "OldFail", audiobookId: 2, completedAt: Now.AddHours(-48), error: "disk full"),
                Make(DownloadStatus.Downloading, title: "Active", audiobookId: 3),
            };

            var result = ActivitySummary.Build(downloads, Now, windowHours: 24, filter: ActivityCategory.Failed);

            var item = Assert.Single(result.Items);
            Assert.Equal("RecentFail", item.Title); // old failure outside the window is excluded
        }

        [Fact]
        public void Build_Counts_WindowTerminalButNotInProgressOrBlocked()
        {
            var downloads = new[]
            {
                Make(DownloadStatus.Downloading, audiobookId: 1, startedAt: Now.AddDays(-10)), // old but still in progress -> counted
                Make(DownloadStatus.ImportBlocked, audiobookId: 2, startedAt: Now.AddDays(-10)), // old block -> counted
                Make(DownloadStatus.Moved, audiobookId: 3, completedAt: Now.AddHours(-2)), // imported in window
                Make(DownloadStatus.Moved, audiobookId: 4, completedAt: Now.AddDays(-3)), // imported outside window
                Make(DownloadStatus.Failed, audiobookId: 5, completedAt: Now.AddHours(-2), error: "qBittorrent state: missingFiles"),
                Make(DownloadStatus.Failed, audiobookId: 6, completedAt: Now.AddHours(-2), error: "Reaped by stall timer: no progress"),
            };

            var c = ActivitySummary.Build(downloads, Now).Summary;

            Assert.Equal(1, c.InProgress);
            Assert.Equal(1, c.Blocked);
            Assert.Equal(1, c.Imported); // the 3-day-old import drops out
            Assert.Equal(1, c.Failed);
            Assert.Equal(1, c.Stalled);
            Assert.Equal(24, c.WindowHours);
        }

        [Fact]
        public void Build_FailureReasons_GroupedByLeadingSegment()
        {
            var downloads = new[]
            {
                Make(DownloadStatus.Failed, audiobookId: 1, completedAt: Now.AddHours(-1), error: "qBittorrent state: missingFiles"),
                Make(DownloadStatus.Failed, audiobookId: 2, completedAt: Now.AddHours(-1), error: "qBittorrent state: errored"),
                Make(DownloadStatus.Failed, audiobookId: 3, completedAt: Now.AddHours(-1), error: "Reaped by stall timer: no progress"),
            };

            var reasons = ActivitySummary.Build(downloads, Now).Summary.FailureReasons;

            Assert.Equal(2, reasons.Count);
            Assert.Equal("qBittorrent state", reasons[0].Reason); // most common first
            Assert.Equal(2, reasons[0].Count);
        }

        [Fact]
        public void Build_BooksWithoutAudiobookId_CollapseByTitle()
        {
            var downloads = new[]
            {
                Make(DownloadStatus.Failed, title: "Same Title", startedAt: Now.AddDays(-1), error: "x"),
                Make(DownloadStatus.Downloading, title: "same title  ", startedAt: Now.AddMinutes(-5)),
            };

            var result = ActivitySummary.Build(downloads, Now);

            var item = Assert.Single(result.Items);
            Assert.Equal(2, item.AttemptCount);
            Assert.Equal(ActivityCategory.InProgress, item.Category);
        }

        [Fact]
        public void Build_OrdersNewestFirst_AndPages()
        {
            var downloads = Enumerable.Range(0, 5)
                .Select(i => Make(DownloadStatus.Downloading, title: $"B{i}", audiobookId: i, startedAt: Now.AddMinutes(-i)))
                .ToArray();

            var page1 = ActivitySummary.Build(downloads, Now, page: 1, pageSize: 2);

            Assert.Equal(5, page1.TotalItems);
            Assert.Equal(2, page1.Items.Count);
            Assert.Equal("B0", page1.Items[0].Title); // most recent (smallest age) first
            Assert.Equal("B1", page1.Items[1].Title);
        }

        [Fact]
        public void Build_PopulatesReasonAndClientName()
        {
            var downloads = new[]
            {
                Make(DownloadStatus.ImportBlocked, audiobookId: 1, blockReason: "Sample file too short"),
            };

            var item = Assert.Single(ActivitySummary.Build(
                downloads, Now, filter: ActivityCategory.Blocked,
                clientNameResolver: id => id == "qbit" ? "My qBittorrent" : null).Items);

            Assert.Equal("Sample file too short", item.Reason);
            Assert.Equal("My qBittorrent", item.DownloadClientName);
        }

        [Fact]
        public void Build_PopulatesClientType_FromResolver()
        {
            var downloads = new[] { Make(DownloadStatus.Downloading, audiobookId: 1, clientId: "qbit") };

            var item = Assert.Single(ActivitySummary.Build(
                downloads, Now,
                clientTypeResolver: id => id == "qbit" ? "qbittorrent" : null).Items);

            Assert.Equal("qbittorrent", item.DownloadClientType);
        }

        [Fact]
        public void Build_ProjectsAttempts_NewestFirst_WithPerAttemptReasons()
        {
            // A book that was blocked, then failed, then finally moved into the library.
            var downloads = new[]
            {
                Make(DownloadStatus.ImportBlocked, audiobookId: 1, startedAt: Now.AddDays(-3), blockReason: "Sample too short"),
                Make(DownloadStatus.Failed, audiobookId: 1, startedAt: Now.AddDays(-2), error: "qBittorrent state: missingFiles"),
                Make(DownloadStatus.Moved, audiobookId: 1, completedAt: Now.AddHours(-1)),
            };

            // The still-Blocked attempt represents the book (Blocked wins over a terminal Moved),
            // so drill into Blocked to get the row; the attempt list spans all three regardless.
            var item = Assert.Single(ActivitySummary.Build(
                downloads, Now, filter: ActivityCategory.Blocked).Items);

            Assert.Equal(3, item.Attempts.Count);
            // Newest-first: the successful Moved attempt leads, with no error.
            Assert.Equal(ActivityCategory.Imported, item.Attempts[0].Category);
            Assert.Null(item.Attempts[0].Reason);
            // Earlier attempts keep their own faithful reasons.
            Assert.Equal(ActivityCategory.Failed, item.Attempts[1].Category);
            Assert.Equal("qBittorrent state: missingFiles", item.Attempts[1].Reason);
            Assert.Equal(ActivityCategory.Blocked, item.Attempts[2].Category);
            Assert.Equal("Sample too short", item.Attempts[2].Reason);
        }
    }
}
