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
using Listenarr.Api.Controllers;
using Listenarr.Application.Audiobooks;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Application.Search;
using Listenarr.Domain.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Api.Controllers
{
    public class LibraryController_NotAudiobookTests
    {
        [Fact]
        public async Task RejectNotAudiobook_AudiobookNotFound_Returns404()
        {
            var (controller, _, _, _) = CreateController(audiobookId: 1, audiobook: null);

            var result = await controller.RejectNotAudiobook(id: 1);

            Assert.IsType<NotFoundObjectResult>(result);
        }

        [Fact]
        public async Task RejectNotAudiobook_RemovesEveryFile_ReMonitors_AndStartsSearch()
        {
            // Unmonitored book with two wrong (non-audiobook) files imported.
            var book = new Audiobook { Id = 7, Title = "Titans", Monitored = false };
            var files = new List<AudiobookFile>
            {
                new() { Id = 100, AudiobookId = 7, Path = "/library/Titans/a.mp3" },
                new() { Id = 101, AudiobookId = 7, Path = "/library/Titans/b.mp3" },
            };

            var (controller, repo, svc, invoker) = CreateController(
                audiobookId: 7, audiobook: book, files: files, queuedOnSearch: 1);

            var result = await controller.RejectNotAudiobook(id: 7);

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.NotNull(ok.Value);

            // Every tracked file is removed from disk + DB.
            svc.Verify(s => s.DeleteAudiobookFileAsync(
                    It.IsAny<Audiobook>(), 100, true, "not-audiobook", It.IsAny<CancellationToken>()),
                Times.Once);
            svc.Verify(s => s.DeleteAudiobookFileAsync(
                    It.IsAny<Audiobook>(), 101, true, "not-audiobook", It.IsAny<CancellationToken>()),
                Times.Once);

            // The book is re-monitored so the re-search can fill it, and a fresh search runs.
            Assert.True(book.Monitored);
            repo.Verify(r => r.UpdateAsync(It.Is<Audiobook>(a => a.Id == 7 && a.Monitored)), Times.Once);
            invoker.Verify(i => i.SearchAudiobookNowAsync(7, It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task RejectNotAudiobook_NoSearchInvoker_StillRemovesFiles_AndSucceeds()
        {
            // The search invoker is optional; without it the files are still removed and the book is
            // left monitored for the next automatic-search cycle (no 500).
            var book = new Audiobook { Id = 9, Title = "Titans", Monitored = true };
            var files = new List<AudiobookFile> { new() { Id = 200, AudiobookId = 9, Path = "/x/a.mp3" } };

            var (controller, _, svc, _) = CreateController(
                audiobookId: 9, audiobook: book, files: files, registerInvoker: false);

            var result = await controller.RejectNotAudiobook(id: 9);

            Assert.IsType<OkObjectResult>(result);
            svc.Verify(s => s.DeleteAudiobookFileAsync(
                    It.IsAny<Audiobook>(), 200, true, "not-audiobook", It.IsAny<CancellationToken>()),
                Times.Once);
        }

        private static (
            LibraryController controller,
            Mock<IAudiobookRepository> repo,
            Mock<IAudiobookFileService> svc,
            Mock<IAutomaticSearchInvoker> invoker) CreateController(
            int audiobookId,
            Audiobook? audiobook,
            List<AudiobookFile>? files = null,
            int queuedOnSearch = 0,
            bool registerInvoker = true)
        {
            var repo = new Mock<IAudiobookRepository>();
            repo.Setup(r => r.GetByIdAsync(audiobookId)).ReturnsAsync(audiobook);
            repo.Setup(r => r.UpdateAsync(It.IsAny<Audiobook>())).ReturnsAsync(true);

            var fileRepo = new Mock<IAudiobookFileRepository>();
            fileRepo.Setup(r => r.GetByAudiobookIdAsync(audiobookId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(files ?? new List<AudiobookFile>());

            var svc = new Mock<IAudiobookFileService>();
            svc.Setup(s => s.DeleteAudiobookFileAsync(
                    It.IsAny<Audiobook>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new DeleteAudiobookFileResult
                {
                    Outcome = DeleteAudiobookFileOutcome.Deleted,
                    DeletedFromDisk = true,
                    Warnings = Array.Empty<string>()
                });

            var invoker = new Mock<IAutomaticSearchInvoker>();
            invoker.Setup(i => i.SearchAudiobookNowAsync(audiobookId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new AutomaticSearchBookResult { Success = true, DownloadsQueued = queuedOnSearch });

            var services = new ServiceCollection();
            services.AddSingleton(svc.Object);
            if (registerInvoker)
            {
                services.AddSingleton(invoker.Object);
            }
            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            var controller = new LibraryController(
                repo.Object,
                new Mock<IImageCacheService>().Object,
                new Mock<ILogger<LibraryController>>().Object,
                scopeFactory,
                new Mock<IHistoryRepository>().Object,
                fileRepo.Object,
                new Mock<IQualityProfileRepository>().Object,
                new Mock<IDownloadRepository>().Object,
                new Mock<IRootFolderRepository>().Object,
                new Mock<IFileNamingService>().Object,
                new Mock<IApplicationPathService>().Object,
                new Mock<ILibraryListService>().Object,
                scanQueueService: null,
                moveQueueService: null,
                notificationService: null,
                rootFolderService: null);

            controller.ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            };

            return (controller, repo, svc, invoker);
        }
    }
}
