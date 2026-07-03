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
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Api.Features.Library
{
    [Trait("Area", "LibraryApi")]
    [Trait("Name", "LibraryController_FileStreamTests")]
    [Trait("Category", "LibraryController")]
    public class LibraryController_FileStreamTests : BaseTests
    {
        private async Task<(Audiobook book, AudiobookFile file, string rootDir)> CreateBookWithFileUnderRootAsync()
        {
            var rootDir = FileService.GetTempDirectory("stream-root");
            await _rootFolderRepository.AddAsync(new RootFolder { Path = rootDir, Name = "stream-root" });

            var bookDir = Path.Join(rootDir, "Book");
            Directory.CreateDirectory(bookDir);
            var audioPath = Path.Join(bookDir, "part1.mp3");
            await File.WriteAllTextAsync(audioPath, "audio-bytes");

            var book = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Streamable")
                .WithBasePath(bookDir)
                .Build());
            var file = await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(book)
                .WithPath(audioPath)
                .Build());
            return (book, file, rootDir);
        }

        [Fact]
        [Trait("Method", "StreamAudiobookFile")]
        [Trait("Scenario", "StreamsFileWithRangeSupport")]
        public async Task StreamAudiobookFile_StreamsFile_WithRangeSupport()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var (book, file, _) = await CreateBookWithFileUnderRootAsync();

            var result = await controller.StreamAudiobookFile(book.Id, file.Id, CancellationToken.None);

            var physical = Assert.IsType<PhysicalFileResult>(result);
            Assert.Equal("audio/mpeg", physical.ContentType);
            Assert.True(physical.EnableRangeProcessing);
            Assert.Equal(Path.GetFullPath(file.Path!), physical.FileName);
        }

        [Fact]
        [Trait("Method", "StreamAudiobookFile")]
        [Trait("Scenario", "RejectsFileOfAnotherAudiobook")]
        public async Task StreamAudiobookFile_RejectsFileOfAnotherAudiobook()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var (_, file, rootDir) = await CreateBookWithFileUnderRootAsync();
            var other = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Other")
                .WithBasePath(Path.Join(rootDir, "Other"))
                .Build());

            var result = await controller.StreamAudiobookFile(other.Id, file.Id, CancellationToken.None);

            Assert.IsType<NotFoundObjectResult>(result);
        }

        [Fact]
        [Trait("Method", "StreamAudiobookFile")]
        [Trait("Scenario", "RejectsFileOutsideRootFolders")]
        public async Task StreamAudiobookFile_RejectsFileOutsideRootFolders()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            // Root folder exists but the file lives elsewhere on disk.
            await _rootFolderRepository.AddAsync(new RootFolder { Path = FileService.GetTempDirectory("stream-root-2"), Name = "r2" });

            var outsideDir = FileService.GetTempDirectory("outside-roots");
            var outsidePath = Path.Join(outsideDir, "leak.mp3");
            await File.WriteAllTextAsync(outsidePath, "audio-bytes");

            var book = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Outside")
                .WithBasePath(outsideDir)
                .Build());
            var file = await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(book)
                .WithPath(outsidePath)
                .Build());

            var result = await controller.StreamAudiobookFile(book.Id, file.Id, CancellationToken.None);

            Assert.IsType<NotFoundObjectResult>(result);
        }

        [Fact]
        [Trait("Method", "StreamAudiobookFile")]
        [Trait("Scenario", "RejectsUnsupportedExtension")]
        public async Task StreamAudiobookFile_RejectsUnsupportedExtension()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var rootDir = FileService.GetTempDirectory("stream-root-3");
            await _rootFolderRepository.AddAsync(new RootFolder { Path = rootDir, Name = "r3" });
            var bookDir = Path.Join(rootDir, "Book");
            Directory.CreateDirectory(bookDir);
            var aaxPath = Path.Join(bookDir, "drm.aax");
            await File.WriteAllTextAsync(aaxPath, "drm-bytes");

            var book = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Drm")
                .WithBasePath(bookDir)
                .Build());
            var file = await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(book)
                .WithPath(aaxPath)
                .Build());

            var result = await controller.StreamAudiobookFile(book.Id, file.Id, CancellationToken.None);

            var obj = Assert.IsType<ObjectResult>(result);
            Assert.Equal(415, obj.StatusCode);
        }

        [Fact]
        [Trait("Method", "GetAudiobook")]
        [Trait("Scenario", "DetailPayloadIncludesVerificationFields")]
        public async Task GetAudiobook_DetailPayload_IncludesVerificationFields()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var book = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Verified Book")
                .WithBasePath(FileService.GetTempDirectory("detail-verif"))
                .Build());
            book.VerificationStatus = VerificationStatus.AgentVerified;
            book.VerificationConfidence = 0.9;
            book.VerificationTranscript = "the audio says things";
            book.VerificationMethod = "deterministic:whisper-base.en";
            await _audiobookRepository.UpdateAsync(book);

            var result = await controller.GetAudiobook(book.Id);

            var ok = Assert.IsType<OkObjectResult>(result.Result);
            var payload = JsonSerializer.SerializeToElement(ok.Value);
            // The detail card renders from these — a projection that drops them
            // leaves the whole verification card invisible (the slice-1 bug).
            Assert.Equal((int)VerificationStatus.AgentVerified, payload.GetProperty("verificationStatus").GetInt32());
            Assert.Equal(0.9, payload.GetProperty("verificationConfidence").GetDouble(), 3);
            Assert.Equal("the audio says things", payload.GetProperty("verificationTranscript").GetString());
            Assert.Equal("deterministic:whisper-base.en", payload.GetProperty("verificationMethod").GetString());
            Assert.True(payload.TryGetProperty("verificationDetailJson", out _));
        }
    }
}
