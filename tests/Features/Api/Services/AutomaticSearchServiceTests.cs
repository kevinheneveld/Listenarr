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
using Listenarr.Application.Interfaces;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Application.Search;
using Listenarr.Application.Search.Filters;
using Listenarr.Domain.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Api.Services
{
    /// <summary>
    /// Characterization tests for the on-demand "Search now" entry point (used by the per-series
    /// Search now endpoint). These pin the contract that matters: it reuses the background cycle's
    /// per-book logic, so it must NOT start a download for a book that already meets the quality
    /// cutoff or has an active download. (A naive fan-out over searchAndDownload would re-grab them.)
    /// </summary>
    public class AutomaticSearchServiceTests
    {
        private static (AutomaticSearchService svc, Mock<IDownloadService> download) BuildService(
            Audiobook audiobook,
            List<Download> downloads,
            List<AudiobookFile> files)
        {
            var audiobookRepo = new Mock<IAudiobookRepository>();
            audiobookRepo.Setup(r => r.GetByIdAsync(audiobook.Id)).ReturnsAsync(audiobook);
            audiobookRepo.Setup(r => r.UpdateAsync(It.IsAny<Audiobook>())).ReturnsAsync(true);

            var downloadRepo = new Mock<IDownloadRepository>();
            downloadRepo.Setup(r => r.GetByAudiobookIdAsync(audiobook.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(downloads);

            var fileRepo = new Mock<IAudiobookFileRepository>();
            fileRepo.Setup(r => r.GetByAudiobookIdAsync(audiobook.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(files);

            var search = new Mock<ISearchService>();
            var download = new Mock<IDownloadService>();
            var qualityProfileService = new Mock<IQualityProfileService>();

            var pipeline = new SearchResultFilterPipeline(
                System.Array.Empty<ISearchResultFilter>(),
                NullLogger<SearchResultFilterPipeline>.Instance);

            var services = new ServiceCollection();
            services.AddSingleton(audiobookRepo.Object);
            services.AddSingleton(downloadRepo.Object);
            services.AddSingleton(fileRepo.Object);
            services.AddSingleton(search.Object);
            services.AddSingleton(download.Object);
            services.AddSingleton(qualityProfileService.Object);
            services.AddSingleton(pipeline);

            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            var svc = new AutomaticSearchService(NullLogger<AutomaticSearchService>.Instance, scopeFactory);
            return (svc, download);
        }

        private static QualityProfile M4bProfile() => new()
        {
            CutoffQuality = "M4B",
            Qualities = new List<QualityDefinition>
            {
                new() { Quality = "M4B", Priority = 10 },
                new() { Quality = "MP3 128kbps", Priority = 1 },
            }
        };

        [Fact]
        public async Task SearchAudiobookNowAsync_SkipsWhenActiveDownloadExists()
        {
            var audiobook = new Audiobook { Id = 42, Title = "Test Book", QualityProfile = M4bProfile() };
            var downloads = new List<Download> { new() { Status = DownloadStatus.Downloading } };

            var (svc, download) = BuildService(audiobook, downloads, new List<AudiobookFile>());

            var result = await svc.SearchAudiobookNowAsync(42);

            Assert.True(result.Success);
            Assert.Equal(0, result.DownloadsQueued);
            download.Verify(d => d.StartDownloadAsync(
                It.IsAny<SearchResult>(), It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
        }

        [Fact]
        public async Task SearchAudiobookNowAsync_SkipsWhenQualityCutoffMet()
        {
            var audiobook = new Audiobook { Id = 43, Title = "Test Book", QualityProfile = M4bProfile() };
            // An existing M4B file meets the M4B cutoff, so no search/grab should happen.
            var files = new List<AudiobookFile> { new() { Path = "/library/test.m4b", Container = "m4b" } };

            var (svc, download) = BuildService(audiobook, new List<Download>(), files);

            var result = await svc.SearchAudiobookNowAsync(43);

            Assert.True(result.Success);
            Assert.Equal(0, result.DownloadsQueued);
            download.Verify(d => d.StartDownloadAsync(
                It.IsAny<SearchResult>(), It.IsAny<string>(), It.IsAny<int?>()), Times.Never);
        }

        [Fact]
        public async Task SearchAudiobookNowAsync_ReturnsNotFoundForUnknownId()
        {
            var (svc, _) = BuildService(
                new Audiobook { Id = 1, Title = "Seed" }, new List<Download>(), new List<AudiobookFile>());

            // Id 999 is never set up on the repo mock, so GetByIdAsync returns null.
            var result = await svc.SearchAudiobookNowAsync(999);

            Assert.False(result.Success);
            Assert.Equal("Audiobook not found", result.Message);
        }
    }
}
