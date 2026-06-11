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
using Listenarr.Application.Audiobooks.Verification;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Domain.Models;
using Listenarr.Domain.Models.Enumerations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Api.Controllers
{
    public class LibraryController_TransferFilesTests
    {
        [Fact]
        public async Task TransferFiles_ReassignsRows_ResetsBothVerdicts_AndEnqueuesVerification()
        {
            // The live shape: wrong-content files on a flagged record, while the
            // book they actually belong to exists as a wanted entry.
            var source = new Audiobook
            {
                Id = 1,
                Title = "Arcanum Unbounded",
                FilePath = "/lib/arcanum/wb2.mp3",
                FileSize = 1234,
                VerificationStatus = VerificationStatus.AgentFlagged,
                VerificationTranscript = "Warbreaker, by Brandon Sanderson",
                VerificationDetailJson = "{\"outcome\":\"uncertain\"}"
            };
            var target = new Audiobook
            {
                Id = 2,
                Title = "Warbreaker (2 of 3)",
                VerificationStatus = VerificationStatus.Unverified
                // No BasePath: the disk move is skipped; ownership still transfers.
            };
            var files = new List<AudiobookFile>
            {
                new() { Id = 10, AudiobookId = 1, Path = "/lib/arcanum/wb2.mp3" },
            };

            var (controller, repo, fileRepo, queue) = CreateController(source, target, files);

            var result = await controller.TransferFiles(1, new LibraryController.TransferFilesRequest(2, null));

            Assert.IsType<OkObjectResult>(result);

            // Row reassigned to the target.
            Assert.Equal(2, files[0].AudiobookId);
            fileRepo.Verify(r => r.UpdateAsync(files[0], It.IsAny<CancellationToken>()), Times.Once);

            // Source lost all audio: legacy columns + verdict reset.
            Assert.Null(source.FilePath);
            Assert.Null(source.FileSize);
            Assert.Equal(VerificationStatus.Unverified, source.VerificationStatus);
            Assert.Null(source.VerificationTranscript);
            Assert.Null(source.VerificationDetailJson);

            // Target re-verified against its new audio with the transfer trigger.
            queue.Verify(q => q.EnqueueAsync(
                It.Is<List<int>>(ids => ids.Count == 1 && ids[0] == 2), VerificationTriggers.Transfer), Times.Once);

            repo.Verify(r => r.UpdateAsync(source), Times.Once);
        }

        [Fact]
        public async Task TransferFiles_PartialTransfer_KeepsSourceVerdict()
        {
            var source = new Audiobook
            {
                Id = 1,
                Title = "Source",
                VerificationStatus = VerificationStatus.AgentFlagged,
                VerificationTranscript = "transcript"
            };
            var target = new Audiobook { Id = 2, Title = "Target" };
            var files = new List<AudiobookFile>
            {
                new() { Id = 10, AudiobookId = 1, Path = "/lib/s/a.mp3" },
                new() { Id = 11, AudiobookId = 1, Path = "/lib/s/b.mp3" },
            };

            var (controller, _, fileRepo, _) = CreateController(source, target, files);

            var result = await controller.TransferFiles(
                1, new LibraryController.TransferFilesRequest(2, new List<int> { 10 }));

            Assert.IsType<OkObjectResult>(result);
            Assert.Equal(2, files[0].AudiobookId);
            Assert.Equal(1, files[1].AudiobookId);
            fileRepo.Verify(r => r.UpdateAsync(It.IsAny<AudiobookFile>(), It.IsAny<CancellationToken>()), Times.Once);

            // Source still holds audio — its verdict (for the remaining file) stays.
            Assert.Equal(VerificationStatus.AgentFlagged, source.VerificationStatus);
            Assert.NotNull(source.VerificationTranscript);
        }

        [Fact]
        public async Task TransferFiles_ManuallyVerifiedTarget_KeepsManualState()
        {
            var source = new Audiobook { Id = 1, Title = "Source" };
            var target = new Audiobook
            {
                Id = 2,
                Title = "Target",
                VerificationStatus = VerificationStatus.ManuallyVerified,
                VerifiedBy = "user"
            };
            var files = new List<AudiobookFile> { new() { Id = 10, AudiobookId = 1, Path = "/lib/s/a.mp3" } };

            var (controller, _, _, _) = CreateController(source, target, files);

            var result = await controller.TransferFiles(1, new LibraryController.TransferFilesRequest(2, null));

            Assert.IsType<OkObjectResult>(result);
            // Manual states are sticky against everything that isn't a human ruling.
            Assert.Equal(VerificationStatus.ManuallyVerified, target.VerificationStatus);
        }

        [Fact]
        public async Task TransferFiles_SameSourceAndTarget_IsRejected()
        {
            var source = new Audiobook { Id = 1, Title = "Source" };
            var (controller, _, _, _) = CreateController(source, target: null, files: new List<AudiobookFile>());

            var result = await controller.TransferFiles(1, new LibraryController.TransferFilesRequest(1, null));

            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Fact]
        public async Task TransferFiles_ForeignFileId_IsRejected()
        {
            var source = new Audiobook { Id = 1, Title = "Source" };
            var target = new Audiobook { Id = 2, Title = "Target" };
            var files = new List<AudiobookFile> { new() { Id = 10, AudiobookId = 1, Path = "/lib/s/a.mp3" } };

            var (controller, _, fileRepo, _) = CreateController(source, target, files);

            var result = await controller.TransferFiles(
                1, new LibraryController.TransferFilesRequest(2, new List<int> { 10, 999 }));

            Assert.IsType<BadRequestObjectResult>(result);
            fileRepo.Verify(r => r.UpdateAsync(It.IsAny<AudiobookFile>(), It.IsAny<CancellationToken>()), Times.Never);
        }

        private static (
            LibraryController controller,
            Mock<IAudiobookRepository> repo,
            Mock<IAudiobookFileRepository> fileRepo,
            Mock<ILibraryVerificationQueueService> queue) CreateController(
            Audiobook source,
            Audiobook? target,
            List<AudiobookFile> files)
        {
            var repo = new Mock<IAudiobookRepository>();
            repo.Setup(r => r.GetByIdAsync(source.Id)).ReturnsAsync(source);
            if (target != null)
            {
                repo.Setup(r => r.GetByIdAsync(target.Id)).ReturnsAsync(target);
            }
            repo.Setup(r => r.UpdateAsync(It.IsAny<Audiobook>())).ReturnsAsync(true);

            var fileRepo = new Mock<IAudiobookFileRepository>();
            fileRepo.Setup(r => r.GetByAudiobookIdAsync(source.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(files);

            var fileMover = new Mock<IFileMover>();
            fileMover.Setup(m => m.PerformActionOn(It.IsAny<FileAction>(), It.IsAny<string>(), It.IsAny<string?>()))
                .ReturnsAsync(true);

            var whisper = new Mock<IWhisperService>();
            whisper.Setup(w => w.IsAvailableAsync()).ReturnsAsync(true);

            var queue = new Mock<ILibraryVerificationQueueService>();
            queue.Setup(q => q.EnqueueAsync(It.IsAny<List<int>?>(), It.IsAny<string>()))
                .ReturnsAsync(Guid.NewGuid());

            var services = new ServiceCollection();
            services.AddSingleton(fileMover.Object);
            services.AddSingleton(whisper.Object);
            services.AddSingleton(queue.Object);
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

            return (controller, repo, fileRepo, queue);
        }
    }
}
