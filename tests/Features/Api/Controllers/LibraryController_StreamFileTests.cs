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
using Listenarr.Application.Common;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Domain.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Api.Controllers
{
    // Covers GET /library/{id}/files/{fileId}/stream — the endpoint that
    // serves a raw audio file to the browser's <audio> element for narrator-
    // identification previews. Range processing is enabled in the controller
    // so the browser can scrub long files; what these tests cover is the
    // security / shape concerns: the file must belong to the requested
    // audiobook, must sit under a configured root folder, must use a
    // browser-playable extension, and must actually exist on disk.
    public class LibraryController_StreamFileTests : IDisposable
    {
        private readonly string _tempRoot;
        private readonly List<string> _filesToCleanup = new();

        public LibraryController_StreamFileTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "listenarr-stream-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
        }

        public void Dispose()
        {
            try { Directory.Delete(_tempRoot, true); }
            catch (IOException) { /* best effort */ }
            catch (UnauthorizedAccessException) { /* best effort */ }
        }

        private string CreateTempAudio(string filename, byte[]? bytes = null)
        {
            var path = Path.Combine(_tempRoot, filename);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes ?? new byte[] { 0xFF, 0xFB, 0x90, 0x00 }); // 4-byte placeholder
            _filesToCleanup.Add(path);
            return path;
        }

        private LibraryController BuildController(
            AudiobookFile? file,
            IEnumerable<string> rootFolderPaths)
        {
            var mockRepo = new Mock<IAudiobookRepository>();
            var mockFiles = new Mock<IAudiobookFileRepository>();
            if (file != null)
            {
                mockFiles.Setup(r => r.GetByIdAsync(file.Id, It.IsAny<CancellationToken>())).ReturnsAsync(file);
            }

            var mockRoots = new Mock<IRootFolderRepository>();
            mockRoots
                .Setup(r => r.GetAllAsync())
                .ReturnsAsync(rootFolderPaths.Select(p => new RootFolder { Path = p }).ToList());

            var mockImageCache = new Mock<IImageCacheService>();
            var mockLogger = new Mock<ILogger<LibraryController>>();
            var mockFileNaming = new Mock<IFileNamingService>();
            var services = new ServiceCollection();
            var provider = services.BuildServiceProvider();
            var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();

            return new LibraryController(
                mockRepo.Object,
                mockImageCache.Object,
                mockLogger.Object,
                scopeFactory,
                new Mock<IHistoryRepository>().Object,
                mockFiles.Object,
                new Mock<IQualityProfileRepository>().Object,
                new Mock<IDownloadRepository>().Object,
                mockRoots.Object,
                mockFileNaming.Object,
                new Mock<IApplicationPathService>().Object,
                new Mock<ILibraryListService>().Object);
        }

        [Fact]
        public async Task StreamAudiobookFile_ReturnsPhysicalFile_OnHappyPath()
        {
            var diskPath = CreateTempAudio("Some Book/Chapter 01.mp3");
            var file = new AudiobookFile { Id = 11, AudiobookId = 1, Path = diskPath };

            var controller = BuildController(file, new[] { _tempRoot });

            var result = await controller.StreamAudiobookFile(1, 11);

            var phys = Assert.IsType<PhysicalFileResult>(result);
            Assert.Equal("audio/mpeg", phys.ContentType);
            Assert.True(phys.EnableRangeProcessing, "Range processing must be on so the browser can scrub.");
            Assert.Equal(Path.GetFullPath(diskPath), phys.FileName);
        }

        [Theory]
        [InlineData("track.mp3", "audio/mpeg")]
        [InlineData("track.m4a", "audio/mp4")]
        [InlineData("book.m4b", "audio/mp4")]
        [InlineData("clip.flac", "audio/flac")]
        [InlineData("song.ogg", "audio/ogg")]
        [InlineData("clip.opus", "audio/ogg")]
        [InlineData("clip.wav", "audio/wav")]
        public async Task StreamAudiobookFile_MapsContentTypeByExtension(string filename, string expected)
        {
            var diskPath = CreateTempAudio(filename);
            var file = new AudiobookFile { Id = 12, AudiobookId = 2, Path = diskPath };

            var controller = BuildController(file, new[] { _tempRoot });

            var result = await controller.StreamAudiobookFile(2, 12);

            var phys = Assert.IsType<PhysicalFileResult>(result);
            Assert.Equal(expected, phys.ContentType);
        }

        [Fact]
        public async Task StreamAudiobookFile_Rejects_WhenFileBelongsToDifferentAudiobook()
        {
            // Critical: prevents using a known file id under an arbitrary
            // audiobook URL to enumerate library contents.
            var diskPath = CreateTempAudio("Other Book/clip.mp3");
            var file = new AudiobookFile { Id = 13, AudiobookId = 999, Path = diskPath };

            var controller = BuildController(file, new[] { _tempRoot });

            // Caller asks for the file under audiobook id 1, but the file
            // belongs to 999. Must 404.
            var result = await controller.StreamAudiobookFile(1, 13);

            Assert.IsType<NotFoundObjectResult>(result);
        }

        [Fact]
        public async Task StreamAudiobookFile_Rejects_WhenFileIdDoesNotExist()
        {
            // No file mocked at all — repository returns null.
            var controller = BuildController(file: null, rootFolderPaths: new[] { _tempRoot });

            var result = await controller.StreamAudiobookFile(1, 9999);

            Assert.IsType<NotFoundObjectResult>(result);
        }

        [Fact]
        public async Task StreamAudiobookFile_Rejects_WhenPathFallsOutsideEveryRootFolder()
        {
            // Path traversal defense: even if a malicious or buggy upstream
            // wrote /etc/passwd into AudiobookFile.Path, the controller must
            // refuse because it isn't under any configured root.
            var file = new AudiobookFile
            {
                Id = 14,
                AudiobookId = 3,
                Path = "/etc/passwd",
            };

            // Configured root is _tempRoot — /etc/passwd is not under it.
            var controller = BuildController(file, new[] { _tempRoot });

            var result = await controller.StreamAudiobookFile(3, 14);

            Assert.IsType<NotFoundObjectResult>(result);
        }

        [Fact]
        public async Task StreamAudiobookFile_Rejects_WhenNoRootFoldersConfigured()
        {
            // Belt-and-suspenders: with zero configured roots, every path is
            // outside the allowlist. Don't accidentally serve anything.
            var diskPath = CreateTempAudio("Some Book/clip.mp3");
            var file = new AudiobookFile { Id = 15, AudiobookId = 4, Path = diskPath };

            var controller = BuildController(file, Array.Empty<string>());

            var result = await controller.StreamAudiobookFile(4, 15);

            Assert.IsType<NotFoundObjectResult>(result);
        }

        [Fact]
        public async Task StreamAudiobookFile_Returns415_OnUnsupportedExtension()
        {
            // .aax is the canonical "we won't try to stream this" case
            // (DRM-protected, browsers can't decode). Use it as the test
            // fixture for any unsupported-by-design extension.
            var diskPath = CreateTempAudio("DRM/protected.aax");
            var file = new AudiobookFile { Id = 16, AudiobookId = 5, Path = diskPath };

            var controller = BuildController(file, new[] { _tempRoot });

            var result = await controller.StreamAudiobookFile(5, 16);

            var status = Assert.IsType<ObjectResult>(result);
            Assert.Equal(415, status.StatusCode);
        }

        [Fact]
        public async Task StreamAudiobookFile_Returns404_WhenFileMissingOnDisk()
        {
            // DB row points at a real-looking path, but the file isn't there.
            // Common drift scenario when files are moved outside Listenarr.
            var ghostPath = Path.Combine(_tempRoot, "Missing Book/gone.mp3");
            // Note: don't actually create the file.
            var file = new AudiobookFile { Id = 17, AudiobookId = 6, Path = ghostPath };

            var controller = BuildController(file, new[] { _tempRoot });

            var result = await controller.StreamAudiobookFile(6, 17);

            Assert.IsType<NotFoundObjectResult>(result);
        }

        [Fact]
        public async Task StreamAudiobookFile_Returns404_WhenFilePathIsEmpty()
        {
            var file = new AudiobookFile { Id = 18, AudiobookId = 7, Path = "" };

            var controller = BuildController(file, new[] { _tempRoot });

            var result = await controller.StreamAudiobookFile(7, 18);

            Assert.IsType<NotFoundObjectResult>(result);
        }
    }
}
