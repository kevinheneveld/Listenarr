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
using Listenarr.Application.Common;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Domain.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Api.Controllers
{
    public class LibraryController_DeleteAudiobookFileTests
    {
        [Fact]
        public async Task DeleteAudiobookFile_AudiobookNotFound_Returns404()
        {
            var (controller, _) = CreateController(audiobookId: 1, audiobook: null);

            var result = await controller.DeleteAudiobookFile(id: 1, fileId: 10, deleteFromDisk: false);

            Assert.IsType<NotFoundObjectResult>(result);
        }

        [Fact]
        public async Task DeleteAudiobookFile_FileNotFound_Returns404()
        {
            var book = new Audiobook { Id = 1, Title = "Book" };
            var (controller, _) = CreateController(audiobookId: 1, audiobook: book,
                serviceResult: DeleteAudiobookFileResult.NotFound());

            var result = await controller.DeleteAudiobookFile(id: 1, fileId: 99, deleteFromDisk: false);

            Assert.IsType<NotFoundObjectResult>(result);
        }

        [Fact]
        public async Task DeleteAudiobookFile_FileBelongsToDifferentAudiobook_Returns400()
        {
            var book = new Audiobook { Id = 1, Title = "Book" };
            var (controller, _) = CreateController(audiobookId: 1, audiobook: book,
                serviceResult: DeleteAudiobookFileResult.WrongAudiobook("/x/y.m4b"));

            var result = await controller.DeleteAudiobookFile(id: 1, fileId: 22, deleteFromDisk: false);

            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Fact]
        public async Task DeleteAudiobookFile_Success_Returns200WithPayload()
        {
            var book = new Audiobook { Id = 1, Title = "Book" };
            var serviceResult = new DeleteAudiobookFileResult
            {
                Outcome = DeleteAudiobookFileOutcome.Deleted,
                DeletedFromDisk = true,
                Path = "/library/Book/track.m4b",
                Warnings = Array.Empty<string>()
            };
            var (controller, serviceMock) = CreateController(audiobookId: 1, audiobook: book,
                serviceResult: serviceResult);

            var result = await controller.DeleteAudiobookFile(id: 1, fileId: 42, deleteFromDisk: true);

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Equal(200, ok.StatusCode ?? 200);

            var deletedFromDiskProp = ok.Value?.GetType().GetProperty("deletedFromDisk")?.GetValue(ok.Value);
            Assert.True((bool)deletedFromDiskProp!);

            var pathProp = ok.Value?.GetType().GetProperty("path")?.GetValue(ok.Value) as string;
            Assert.Equal("/library/Book/track.m4b", pathProp);

            serviceMock.Verify(
                s => s.DeleteAudiobookFileAsync(
                    It.Is<Audiobook>(a => a.Id == 1),
                    42,
                    true,
                    "manual",
                    It.IsAny<CancellationToken>()),
                Times.Once);
        }

        private static (LibraryController controller, Mock<IAudiobookFileService> svc) CreateController(
            int audiobookId,
            Audiobook? audiobook,
            DeleteAudiobookFileResult? serviceResult = null)
        {
            var repo = new Mock<IAudiobookRepository>();
            repo.Setup(r => r.GetByIdAsync(audiobookId)).ReturnsAsync(audiobook);

            var svc = new Mock<IAudiobookFileService>();
            if (serviceResult != null)
            {
                svc.Setup(s => s.DeleteAudiobookFileAsync(
                        It.IsAny<Audiobook>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(serviceResult);
            }

            var services = new ServiceCollection();
            services.AddSingleton(svc.Object);
            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            var controller = new LibraryController(
                repo.Object,
                new Mock<IImageCacheService>().Object,
                new Mock<ILogger<LibraryController>>().Object,
                scopeFactory,
                new Mock<IHistoryRepository>().Object,
                new Mock<IAudiobookFileRepository>().Object,
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

            return (controller, svc);
        }
    }
}
