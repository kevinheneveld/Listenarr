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
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Listenarr.Tests.Features.Application.Downloads
{
    [Trait("Name", "DownloadHistoryRetentionServiceTests")]
    [Trait("Category", "DownloadHistoryRetention")]
    public class DownloadHistoryRetentionServiceTests : BaseTests
    {
        [Fact]
        [Trait("Scenario", "Retention removes old terminal history, keeps recent and active")]
        public async Task RunCleanupAsync_RemovesOldTerminalDownloads_KeepsRecentAndActive()
        {
            var beyond = DateTime.UtcNow.AddDays(-(DownloadHistoryRetentionService.RetentionDays + 1));
            var within = DateTime.UtcNow.AddDays(-1);

            // Old terminal records -> purged. Cover both the CompletedAt path and the
            // StartedAt-fallback path (ImportBlocked rows have no CompletedAt).
            await _downloadRepository.AddAsync(new DownloadBuilder().WithId("old-completed").WithCompletedStatus(at: beyond).Build());
            await _downloadRepository.AddAsync(new DownloadBuilder().WithId("old-moved").WithStatus(DownloadStatus.Moved).WithStartDate(beyond).Build());
            await _downloadRepository.AddAsync(new DownloadBuilder().WithId("old-failed").WithStatus(DownloadStatus.Failed).WithStartDate(beyond).Build());
            await _downloadRepository.AddAsync(new DownloadBuilder().WithId("old-blocked").WithStatus(DownloadStatus.ImportBlocked).WithStartDate(beyond).Build());

            // Recent terminal record -> retained (inside the window).
            await _downloadRepository.AddAsync(new DownloadBuilder().WithId("recent-completed").WithCompletedStatus(at: within).Build());

            // Active records -> never purged regardless of age.
            await _downloadRepository.AddAsync(new DownloadBuilder().WithId("active-downloading").WithStatus(DownloadStatus.Downloading).WithStartDate(beyond).Build());
            await _downloadRepository.AddAsync(new DownloadBuilder().WithId("active-importpending").WithStatus(DownloadStatus.ImportPending).WithStartDate(beyond).Build());

            var service = _provider.GetRequiredService<DownloadHistoryRetentionService>();
            await service.RunCleanupAsync(CancellationToken.None);

            Assert.Null(await _downloadRepository.FindAsync("old-completed"));
            Assert.Null(await _downloadRepository.FindAsync("old-moved"));
            Assert.Null(await _downloadRepository.FindAsync("old-failed"));
            Assert.Null(await _downloadRepository.FindAsync("old-blocked"));

            Assert.NotNull(await _downloadRepository.FindAsync("recent-completed"));
            Assert.NotNull(await _downloadRepository.FindAsync("active-downloading"));
            Assert.NotNull(await _downloadRepository.FindAsync("active-importpending"));
        }

        [Fact]
        [Trait("Scenario", "Retention is a no-op when nothing is past the window")]
        public async Task RunCleanupAsync_NoEligibleDownloads_DoesNothing()
        {
            await _downloadRepository.AddAsync(new DownloadBuilder().WithId("recent-failed").WithStatus(DownloadStatus.Failed).WithStartDate(DateTime.UtcNow.AddHours(-2)).Build());

            var service = _provider.GetRequiredService<DownloadHistoryRetentionService>();
            await service.RunCleanupAsync(CancellationToken.None);

            Assert.NotNull(await _downloadRepository.FindAsync("recent-failed"));
        }
    }
}
