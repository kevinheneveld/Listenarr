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
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;
using Listenarr.Api.Controllers;
using Listenarr.Api.Dtos;
using Listenarr.Domain.Models;
using Listenarr.Tests.Common;
using Listenarr.Application.Common;

namespace Listenarr.Tests.Features.Api.Controllers
{
    /// <summary>
    /// Tests for the duplicate-audiobook preview and merge endpoints
    /// (issue #6). The endpoints find rows sharing a normalized ASIN and
    /// consolidate them into a chosen winner.
    /// </summary>
    public class LibraryController_DuplicatesTests : BaseTests
    {
        private readonly Mock<IImageCacheService> imageCacheServiceMock = new Mock<IImageCacheService>();

        public override async Task InitializeAsync()
        {
            _services.AddSingleton(imageCacheServiceMock.Object);
            Init();
            await Task.CompletedTask;
        }

        [Fact]
        public async Task GetDuplicates_NoLibrary_ReturnsEmpty()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var result = await controller.GetDuplicates() as OkObjectResult;
            Assert.NotNull(result);
        }

        [Fact]
        public async Task GetDuplicates_FindsSameAsinGroup_RecommendsRowWithFile()
        {
            // Two rows share an ASIN; one has files, one is a phantom.
            var phantom = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Test Book",
                Asin = "B00DEDUP001",
                BasePath = "/audiobooks",
                FilePath = null,
            });
            var real = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Test Book",
                Asin = "B00DEDUP001",
                BasePath = "/audiobooks/Test Author/Test Book",
                FilePath = "/audiobooks/Test Author/Test Book/Test Book.mp3",
            });
            // An unrelated row should be ignored.
            await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Solo Book",
                Asin = "B00SOLO0001",
            });

            var controller = _provider.GetRequiredService<LibraryController>();
            var actionResult = await controller.GetDuplicates() as OkObjectResult;
            Assert.NotNull(actionResult);

            // The endpoint returns an anonymous object; reflect into it to
            // pull the groups out.
            var payload = actionResult!.Value!;
            var groupsProp = payload.GetType().GetProperty("groups");
            Assert.NotNull(groupsProp);
            var groups = (System.Collections.IEnumerable)groupsProp!.GetValue(payload)!;
            var groupList = new List<DuplicateGroupDto>();
            foreach (var g in groups) groupList.Add((DuplicateGroupDto)g);

            Assert.Single(groupList);
            var group = groupList[0];
            Assert.Equal("B00DEDUP001", group.NormalizedAsin);
            Assert.Equal(2, group.Rows.Count);

            var recommended = group.Rows.Single(r => r.RecommendedWinner);
            Assert.Equal(real.Id, recommended.Id);

            var phantomDto = group.Rows.Single(r => r.Id == phantom.Id);
            Assert.False(phantomDto.RecommendedWinner);
            Assert.False(phantomDto.HasBookFolder);
            Assert.False(phantomDto.HasAnyFile);
        }

        [Fact]
        public async Task MergeDuplicates_EmptyRequest_ReturnsBadRequest()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var result = await controller.MergeDuplicates(new MergeDuplicatesRequest());
            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Fact]
        public async Task MergeDuplicates_AsinMismatch_AbortsBeforeAnyChange()
        {
            var a = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Real A",
                Asin = "B00MISMATCH1",
            });
            var b = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Real B",
                Asin = "B00MISMATCH2", // different ASIN — refusal expected
            });

            var controller = _provider.GetRequiredService<LibraryController>();
            var result = await controller.MergeDuplicates(new MergeDuplicatesRequest
            {
                Merges =
                {
                    new MergePairDto { WinnerId = a.Id, LoserIds = new List<int> { b.Id } }
                }
            });

            Assert.IsType<BadRequestObjectResult>(result);

            // Neither row should have been touched.
            Assert.NotNull(await _audiobookRepository.GetByIdAsync(a.Id));
            Assert.NotNull(await _audiobookRepository.GetByIdAsync(b.Id));
        }

        [Fact]
        public async Task MergeDuplicates_WinnerWithoutAsin_RefusesToMerge()
        {
            var winner = await _audiobookRepository.AddAsync(new Audiobook { Title = "No ASIN winner", Asin = null });
            var loser = await _audiobookRepository.AddAsync(new Audiobook { Title = "Some loser", Asin = "B00LOSE0001" });

            var controller = _provider.GetRequiredService<LibraryController>();
            var result = await controller.MergeDuplicates(new MergeDuplicatesRequest
            {
                Merges =
                {
                    new MergePairDto { WinnerId = winner.Id, LoserIds = new List<int> { loser.Id } }
                }
            });

            Assert.IsType<BadRequestObjectResult>(result);
            Assert.NotNull(await _audiobookRepository.GetByIdAsync(loser.Id));
        }

        [Fact]
        public async Task MergeDuplicates_DeletesLosers_KeepsWinner_ReassignsHistory()
        {
            // Set up a same-ASIN pair and a History entry referencing the loser.
            var winner = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Winner",
                Asin = "B00MERGE001",
                BasePath = "/audiobooks/Author/Winner",
                FilePath = "/audiobooks/Author/Winner/Winner.mp3",
            });
            var loser = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Loser",
                Asin = "B00MERGE001",
                BasePath = "/audiobooks",
                FilePath = null,
            });

            await _historyRepository.AddAsync(new History
            {
                AudiobookId = loser.Id,
                AudiobookTitle = "Loser",
                EventType = "Added",
                Message = "Test history on loser",
                Timestamp = DateTime.UtcNow,
            });

            var controller = _provider.GetRequiredService<LibraryController>();
            var actionResult = await controller.MergeDuplicates(new MergeDuplicatesRequest
            {
                Merges =
                {
                    new MergePairDto { WinnerId = winner.Id, LoserIds = new List<int> { loser.Id } }
                }
            });

            var ok = actionResult as OkObjectResult;
            Assert.True(ok != null, $"Expected Ok, got {actionResult?.GetType().Name}: {(actionResult as ObjectResult)?.Value}");
            var result = (MergeDuplicatesResultDto)ok!.Value!;
            Assert.Equal(1, result.GroupsProcessed);
            Assert.Equal(1, result.RowsDeleted);
            Assert.Equal(1, result.HistoryReassigned);

            // Winner remains, loser is gone.
            Assert.NotNull(await _audiobookRepository.GetByIdAsync(winner.Id));
            Assert.Null(await _audiobookRepository.GetByIdAsync(loser.Id));

            // History was reassigned to the winner. Resolve via a fresh scope
            // because ExecuteUpdateAsync bypasses change tracking on the
            // already-instantiated test repository's DbContext.
            using var verifyScope = _provider.CreateScope();
            var historyRepo = verifyScope.ServiceProvider
                .GetRequiredService<Listenarr.Application.Interfaces.Repositories.IHistoryRepository>();
            var hist = await historyRepo.GetByAudiobookIdAsync(winner.Id);
            var reassigned = hist.SingleOrDefault(h => h.AudiobookTitle == "Loser");
            Assert.NotNull(reassigned);
            Assert.Equal(winner.Id, reassigned!.AudiobookId);
        }

        [Fact]
        public async Task MergeDuplicates_IgnoresPairsWithOnlySelfAsLoser()
        {
            // A no-op merge (winner is also "loser" of itself) should be
            // rejected as "all merges had no losers" without touching DB.
            var a = await _audiobookRepository.AddAsync(new Audiobook { Title = "A", Asin = "B00NOOP0001" });
            var controller = _provider.GetRequiredService<LibraryController>();
            var result = await controller.MergeDuplicates(new MergeDuplicatesRequest
            {
                Merges =
                {
                    new MergePairDto { WinnerId = a.Id, LoserIds = new List<int> { a.Id } }
                }
            });
            Assert.IsType<BadRequestObjectResult>(result);
            Assert.NotNull(await _audiobookRepository.GetByIdAsync(a.Id));
        }
    }
}
