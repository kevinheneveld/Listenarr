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
using Listenarr.Api.Controllers;
using Listenarr.Application.Common;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Domain.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Api.Controllers
{
    public class LibraryController_FileOrderingTests
    {
        // Regression: the audiobook-detail endpoint constructs its own anonymous-object
        // response (independent of AudiobookDtoFactory), so the natural-sort fix has to
        // live in BOTH places. Previously this endpoint returned files in raw EF order
        // — for a 14-disc rip, the UI showed Disc 08, 01, 06, 10, 07, 09, 11, 12, 03...
        // because that's the order ffprobe / the scan happened to visit them.
        [Fact]
        public async Task GetAudiobook_OrdersMultiDiscFilesNaturallyInResponse()
        {
            var book = new Audiobook
            {
                Id = 42,
                Title = "Command Authority",
                Monitored = true,
                Files = new List<AudiobookFile>()
            };

            // Insert in the same shuffled order the user actually observed in the bug report.
            var insertOrder = new[] { 8, 1, 6, 10, 7, 9, 11, 12, 3, 4, 13, 14, 2, 5 };
            var nextFileId = 1;
            foreach (var disc in insertOrder)
            {
                book.Files.Add(new AudiobookFile
                {
                    Id = nextFileId++,
                    AudiobookId = book.Id,
                    Path = $"/audiobooks/Tom Clancy/Command Authority/Tom Clancy - Command Authority Disc {disc:D2}.mp3",
                    CreatedAt = DateTime.UtcNow
                });
            }

            var mockRepo = new Mock<IAudiobookRepository>();
            mockRepo.Setup(r => r.GetByIdAsync(book.Id)).ReturnsAsync(book);

            using var provider = new ServiceCollection().BuildServiceProvider();
            var controller = new LibraryController(
                mockRepo.Object,
                Mock.Of<IImageCacheService>(),
                NullLogger<LibraryController>.Instance,
                provider.GetRequiredService<IServiceScopeFactory>(),
                Mock.Of<IHistoryRepository>(),
                Mock.Of<IAudiobookFileRepository>(),
                Mock.Of<IQualityProfileRepository>(),
                Mock.Of<IDownloadRepository>(),
                Mock.Of<IRootFolderRepository>(),
                Mock.Of<IFileNamingService>());

            var actionResult = await controller.GetAudiobook(book.Id);
            var ok = Assert.IsType<OkObjectResult>(actionResult.Result);

            var json = JsonSerializer.Serialize(ok.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using var doc = JsonDocument.Parse(json);
            var files = doc.RootElement.GetProperty("files").EnumerateArray().ToList();

            Assert.Equal(14, files.Count);
            for (var i = 0; i < 14; i++)
            {
                var expectedDisc = i + 1;
                var path = files[i].GetProperty("path").GetString();
                Assert.EndsWith($"Disc {expectedDisc:D2}.mp3", path);
            }
        }
    }
}
