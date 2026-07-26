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
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Api.Features.Library
{
    [Trait("Area", "LibraryApi")]
    [Trait("Name", "LibraryController_UploadFilesTests")]
    [Trait("Category", "LibraryController")]
    public class LibraryController_UploadFilesTests : BaseTests
    {
        private static JsonElement ToJson(object? value)
        {
            return JsonSerializer.SerializeToElement(value);
        }

        private async Task<Audiobook> CreateAudiobookWithFolderAsync(string folderName)
        {
            var folder = FileService.GetTempDirectory(folderName);
            return await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("The Tainted Cup")
                .WithBasePath(folder)
                .Build());
        }

        private static FormFile MakeFormFile(string fileName, byte[] content)
        {
            var stream = new MemoryStream(content);
            return new FormFile(stream, 0, stream.Length, "files", fileName)
            {
                Headers = new HeaderDictionary(),
                ContentType = "application/octet-stream"
            };
        }

        private async Task<IActionResult> UploadAsync(int audiobookId, params FormFile[] files)
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var workflow = _provider.GetRequiredService<LibraryUploadWorkflow>();
            var collection = new FormFileCollection();
            collection.AddRange(files);
            return await controller.UploadAudiobookFiles(audiobookId, collection, workflow, CancellationToken.None);
        }

        [Fact]
        [Trait("Method", "UploadAudiobookFiles")]
        [Trait("Scenario", "SavesAndRegistersAudioFile")]
        public async Task Upload_SavesAndRegistersAudioFile()
        {
            var audiobook = await CreateAudiobookWithFolderAsync("upload-happy");
            var content = Encoding.UTF8.GetBytes("fake audio payload");

            var result = await UploadAsync(audiobook.Id, MakeFormFile("01 - Chapter One.mp3", content));

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = ToJson(ok.Value);
            Assert.Equal(1, payload.GetProperty("uploaded").GetInt32());

            var savedPath = Path.Join(audiobook.BasePath, "01 - Chapter One.mp3");
            Assert.True(File.Exists(savedPath));

            var rows = await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id);
            var row = Assert.Single(rows);
            Assert.Equal("01 - Chapter One.mp3", row.Path);
            Assert.Equal("upload", row.Source);
            Assert.Equal(content.Length, row.Size);
        }

        [Fact]
        [Trait("Method", "UploadAudiobookFiles")]
        [Trait("Scenario", "SkipsUnsupportedFileTypes")]
        public async Task Upload_SkipsUnsupportedFileTypes()
        {
            var audiobook = await CreateAudiobookWithFolderAsync("upload-unsupported");

            var result = await UploadAsync(audiobook.Id, MakeFormFile("notes.txt", Encoding.UTF8.GetBytes("text")));

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = ToJson(ok.Value);
            Assert.Equal(0, payload.GetProperty("uploaded").GetInt32());
            Assert.Equal(1, payload.GetProperty("skipped").GetArrayLength());

            Assert.False(File.Exists(Path.Join(audiobook.BasePath, "notes.txt")));
            Assert.Empty(await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id));
        }

        [Fact]
        [Trait("Method", "UploadAudiobookFiles")]
        [Trait("Scenario", "NeutralizesPathTraversalNames")]
        public async Task Upload_NeutralizesPathTraversalNames()
        {
            var audiobook = await CreateAudiobookWithFolderAsync("upload-traversal");
            var content = Encoding.UTF8.GetBytes("fake audio payload");

            var result = await UploadAsync(audiobook.Id, MakeFormFile("../../evil.mp3", content));

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = ToJson(ok.Value);
            Assert.Equal(1, payload.GetProperty("uploaded").GetInt32());

            // The leaf name lands inside the audiobook folder; nothing escapes it.
            Assert.True(File.Exists(Path.Join(audiobook.BasePath, "evil.mp3")));
            var parent = Directory.GetParent(audiobook.BasePath!)!.FullName;
            Assert.False(File.Exists(Path.Join(parent, "evil.mp3")));
            Assert.False(File.Exists(Path.Join(Directory.GetParent(parent)!.FullName, "evil.mp3")));
        }

        [Fact]
        [Trait("Method", "UploadAudiobookFiles")]
        [Trait("Scenario", "ExtractsZipAudioEntriesFlat")]
        public async Task Upload_ExtractsZipAudioEntriesFlat()
        {
            var audiobook = await CreateAudiobookWithFolderAsync("upload-zip");

            using var zipBuffer = new MemoryStream();
            using (var archive = new ZipArchive(zipBuffer, ZipArchiveMode.Create, leaveOpen: true))
            {
                async Task AddEntry(string name, string body)
                {
                    var entry = archive.CreateEntry(name);
                    await using var es = entry.Open();
                    var bytes = Encoding.UTF8.GetBytes(body);
                    await es.WriteAsync(bytes);
                }

                await AddEntry("The Tainted Cup/01.mp3", "track one");
                await AddEntry("../escape.mp3", "zip slip attempt");
                await AddEntry("The Tainted Cup/cover.jpg", "not audio");
            }

            var result = await UploadAsync(audiobook.Id, MakeFormFile("libro-fm-download.zip", zipBuffer.ToArray()));

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = ToJson(ok.Value);
            Assert.Equal(2, payload.GetProperty("uploaded").GetInt32());

            // Entries are flattened to their leaf names inside the book folder.
            Assert.True(File.Exists(Path.Join(audiobook.BasePath, "01.mp3")));
            Assert.True(File.Exists(Path.Join(audiobook.BasePath, "escape.mp3")));
            Assert.False(File.Exists(Path.Join(audiobook.BasePath, "cover.jpg")));
            var parent = Directory.GetParent(audiobook.BasePath!)!.FullName;
            Assert.False(File.Exists(Path.Join(parent, "escape.mp3")));

            var rows = await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id);
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.Equal("upload", r.Source));
        }

        [Fact]
        [Trait("Method", "UploadAudiobookFiles")]
        [Trait("Scenario", "SkipsExactDuplicateAndUniquifiesDifferentContent")]
        public async Task Upload_SkipsExactDuplicate_AndUniquifiesDifferentContent()
        {
            var audiobook = await CreateAudiobookWithFolderAsync("upload-dupes");
            var content = Encoding.UTF8.GetBytes("fake audio payload");

            var first = await UploadAsync(audiobook.Id, MakeFormFile("book.m4b", content));
            Assert.Equal(1, ToJson(Assert.IsType<OkObjectResult>(first).Value).GetProperty("uploaded").GetInt32());

            // Same name + same size → recognized as already present, not re-imported.
            var duplicate = await UploadAsync(audiobook.Id, MakeFormFile("book.m4b", content));
            var dupPayload = ToJson(Assert.IsType<OkObjectResult>(duplicate).Value);
            Assert.Equal(0, dupPayload.GetProperty("uploaded").GetInt32());

            // Same name, different size → kept alongside with a uniquified name.
            var different = await UploadAsync(audiobook.Id, MakeFormFile("book.m4b", Encoding.UTF8.GetBytes("a different, longer audio payload")));
            Assert.Equal(1, ToJson(Assert.IsType<OkObjectResult>(different).Value).GetProperty("uploaded").GetInt32());
            Assert.True(File.Exists(Path.Join(audiobook.BasePath, "book (1).m4b")));

            var rows = await _audiobookFileRepository.GetByAudiobookIdAsync(audiobook.Id);
            Assert.Equal(2, rows.Count);
        }

        [Fact]
        [Trait("Method", "UploadAudiobookFiles")]
        [Trait("Scenario", "ReturnsNotFound_WhenAudiobookMissing")]
        public async Task Upload_ReturnsNotFound_WhenAudiobookMissing()
        {
            var result = await UploadAsync(999_101, MakeFormFile("book.m4b", Encoding.UTF8.GetBytes("audio")));
            Assert.IsType<NotFoundObjectResult>(result);
        }

        [Fact]
        [Trait("Method", "UploadAudiobookFiles")]
        [Trait("Scenario", "ReturnsBadRequest_WhenNoFilesProvided")]
        public async Task Upload_ReturnsBadRequest_WhenNoFilesProvided()
        {
            var audiobook = await CreateAudiobookWithFolderAsync("upload-empty");
            var result = await UploadAsync(audiobook.Id);
            Assert.IsType<BadRequestObjectResult>(result);
        }
    }
}
