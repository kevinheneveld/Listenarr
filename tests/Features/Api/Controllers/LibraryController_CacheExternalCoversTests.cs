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
using Listenarr.Application.Common.Images;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Interfaces.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Api.Controllers
{
    public class LibraryController_CacheExternalCoversTests
    {
        private static LibraryController BuildController(IExternalCoverArtSweepService? sweep)
        {
            var services = new ServiceCollection();
            var provider = services.BuildServiceProvider();
            return new LibraryController(
                new Mock<IAudiobookRepository>().Object,
                new Mock<IImageCacheService>().Object,
                new Mock<ILogger<LibraryController>>().Object,
                provider.GetRequiredService<IServiceScopeFactory>(),
                new Mock<IHistoryRepository>().Object,
                new Mock<IAudiobookFileRepository>().Object,
                new Mock<IQualityProfileRepository>().Object,
                new Mock<IDownloadRepository>().Object,
                new Mock<IRootFolderRepository>().Object,
                new Mock<IFileNamingService>().Object,
                externalCoverArtSweepService: sweep);
        }

        [Fact]
        public async Task CacheExternalCovers_Returns503_WhenServiceMissing()
        {
            var controller = BuildController(sweep: null);

            var result = await controller.CacheExternalCovers(CancellationToken.None);

            var status = Assert.IsType<ObjectResult>(result);
            Assert.Equal(503, status.StatusCode);
        }

        [Fact]
        public async Task CacheExternalCovers_ReturnsOkPayload_WithSweepCounts()
        {
            var sweep = new Mock<IExternalCoverArtSweepService>();
            sweep.Setup(s => s.SweepAsync(It.IsAny<CancellationToken>()))
                .ReturnsAsync(new ExternalCoverArtSweepResult
                {
                    TotalScanned = 10,
                    AlreadyLocal = 6,
                    Queued = 4,
                    Succeeded = 3,
                    Failed = 1,
                    DurationMs = 1234,
                });

            var controller = BuildController(sweep.Object);
            var result = await controller.CacheExternalCovers(CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.NotNull(ok.Value);
            // Anonymous payload — pull values via reflection so the test
            // fails loudly if a property is renamed or dropped from the API.
            var payload = ok.Value!;
            int Get(string name) => (int)payload.GetType().GetProperty(name)!.GetValue(payload)!;
            long GetLong(string name) => (long)payload.GetType().GetProperty(name)!.GetValue(payload)!;
            Assert.Equal(10, Get("totalScanned"));
            Assert.Equal(6, Get("alreadyLocal"));
            Assert.Equal(4, Get("queued"));
            Assert.Equal(3, Get("succeeded"));
            Assert.Equal(1, Get("failed"));
            Assert.Equal(1234L, GetLong("durationMs"));
        }
    }
}
