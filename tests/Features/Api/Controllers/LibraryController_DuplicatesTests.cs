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
using Listenarr.Tests.Builders;
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

        [Fact]
        public async Task MergeDuplicates_ClearAsinOnly_KeepsRowButRemovesAsin()
        {
            // The "wrong ASIN got stamped on this row" case: same ASIN, but
            // the two rows are actually different books. User wants to keep
            // both, just clear the bad ASIN on one of them.
            var winner = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Real book A", Asin = "B00CLEAR0001",
            });
            var wrongAsin = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Different book that got the same ASIN", Asin = "B00CLEAR0001",
            });

            var controller = _provider.GetRequiredService<LibraryController>();
            var actionResult = await controller.MergeDuplicates(new MergeDuplicatesRequest
            {
                Merges =
                {
                    new MergePairDto
                    {
                        WinnerId = winner.Id,
                        LoserIds = new List<int>(),
                        ClearAsinIds = new List<int> { wrongAsin.Id },
                    }
                }
            });

            var ok = actionResult as OkObjectResult;
            Assert.True(ok != null, $"Expected Ok, got {actionResult?.GetType().Name}: {(actionResult as ObjectResult)?.Value}");
            var result = (MergeDuplicatesResultDto)ok!.Value!;
            Assert.Equal(1, result.GroupsProcessed);
            Assert.Equal(0, result.RowsDeleted);
            Assert.Equal(1, result.AsinsCleared);

            // Both rows survive; the cleared one has no ASIN now.
            using var verifyScope = _provider.CreateScope();
            var repo = verifyScope.ServiceProvider
                .GetRequiredService<Listenarr.Application.Interfaces.Repositories.IAudiobookRepository>();
            var winnerNow = await repo.GetByIdAsync(winner.Id);
            var clearedNow = await repo.GetByIdAsync(wrongAsin.Id);
            Assert.NotNull(winnerNow);
            Assert.NotNull(clearedNow);
            Assert.Equal("B00CLEAR0001", winnerNow!.Asin);
            Assert.True(string.IsNullOrEmpty(clearedNow!.Asin), $"Expected null/empty ASIN on cleared row, got '{clearedNow.Asin}'");
        }

        [Fact]
        public async Task MergeDuplicates_ClearAsinOnly_WithoutWinner_StillWorks()
        {
            // Pair with no winner, only ClearAsinIds: the reference ASIN comes
            // from the first clear id. Everyone keeps existing as a row, ASINs
            // are nulled.
            var a = await _audiobookRepository.AddAsync(new Audiobook { Title = "A", Asin = "B00ASINONLY1" });
            var b = await _audiobookRepository.AddAsync(new Audiobook { Title = "B", Asin = "B00ASINONLY1" });

            var controller = _provider.GetRequiredService<LibraryController>();
            var actionResult = await controller.MergeDuplicates(new MergeDuplicatesRequest
            {
                Merges =
                {
                    new MergePairDto
                    {
                        WinnerId = null,
                        LoserIds = new List<int>(),
                        ClearAsinIds = new List<int> { a.Id, b.Id },
                    }
                }
            });

            var ok = actionResult as OkObjectResult;
            Assert.True(ok != null, $"Expected Ok, got {actionResult?.GetType().Name}: {(actionResult as ObjectResult)?.Value}");
            var result = (MergeDuplicatesResultDto)ok!.Value!;
            Assert.Equal(2, result.AsinsCleared);
            Assert.Equal(0, result.RowsDeleted);
        }

        [Fact]
        public async Task MergeDuplicates_LosersWithoutWinner_RefusesToMerge()
        {
            var loser = await _audiobookRepository.AddAsync(new Audiobook { Title = "Loser", Asin = "B00NOWIN0001" });
            var controller = _provider.GetRequiredService<LibraryController>();
            var result = await controller.MergeDuplicates(new MergeDuplicatesRequest
            {
                Merges =
                {
                    new MergePairDto
                    {
                        WinnerId = null,
                        LoserIds = new List<int> { loser.Id },
                    }
                }
            });

            Assert.IsType<BadRequestObjectResult>(result);
            Assert.NotNull(await _audiobookRepository.GetByIdAsync(loser.Id));
        }

        [Fact]
        public async Task MergeDuplicates_ClearAsinMismatch_AbortsBeforeAnyChange()
        {
            var winner = await _audiobookRepository.AddAsync(new Audiobook { Title = "Winner", Asin = "B00CLRMM0001" });
            var unrelated = await _audiobookRepository.AddAsync(new Audiobook { Title = "Unrelated", Asin = "B00CLRMM0002" });

            var controller = _provider.GetRequiredService<LibraryController>();
            var result = await controller.MergeDuplicates(new MergeDuplicatesRequest
            {
                Merges =
                {
                    new MergePairDto
                    {
                        WinnerId = winner.Id,
                        ClearAsinIds = new List<int> { unrelated.Id },
                    }
                }
            });

            Assert.IsType<BadRequestObjectResult>(result);

            // Both keep their original ASINs.
            using var verifyScope = _provider.CreateScope();
            var repo = verifyScope.ServiceProvider
                .GetRequiredService<Listenarr.Application.Interfaces.Repositories.IAudiobookRepository>();
            Assert.Equal("B00CLRMM0002", (await repo.GetByIdAsync(unrelated.Id))!.Asin);
        }

        [Fact]
        public async Task GetDuplicates_PrefersSingleFileOverMultiFile()
        {
            // Two rows with the same ASIN, both have tracked files. One has a
            // single .m4b (Kevin's preferred shape), the other is chunked into
            // many chapter files. The single-file row should win.
            var singleFile = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Whole Book", Asin = "B00SINGLE001",
                BasePath = "/audiobooks/A/Whole Book",
                FilePath = "/audiobooks/A/Whole Book/Book.m4b",
            });
            await _audiobookFileRepository.AddAsync(new AudiobookFile
            {
                AudiobookId = singleFile.Id,
                Path = "/audiobooks/A/Whole Book/Book.m4b",
                CreatedAt = DateTime.UtcNow,
            });
            var multiFile = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Chunked Book", Asin = "B00SINGLE001",
                BasePath = "/audiobooks/A/Chunked Book",
                FilePath = null,
            });
            for (var i = 1; i <= 12; i++)
            {
                await _audiobookFileRepository.AddAsync(new AudiobookFile
                {
                    AudiobookId = multiFile.Id,
                    Path = $"/audiobooks/A/Chunked Book/chapter-{i:00}.mp3",
                    CreatedAt = DateTime.UtcNow,
                });
            }

            var controller = _provider.GetRequiredService<LibraryController>();
            var ok = await controller.GetDuplicates() as OkObjectResult;
            Assert.NotNull(ok);
            var payload = ok!.Value!;
            var groupsObj = payload.GetType().GetProperty("groups")!.GetValue(payload);
            var groupList = new List<DuplicateGroupDto>();
            foreach (var g in (System.Collections.IEnumerable)groupsObj!) groupList.Add((DuplicateGroupDto)g);

            Assert.Single(groupList);
            var recommended = groupList[0].Rows.Single(r => r.RecommendedWinner);
            Assert.Equal(singleFile.Id, recommended.Id);
            Assert.Contains("Single-file", groupList[0].RecommendationReason);
        }

        [Fact]
        public async Task GetDuplicates_DetectsIntraRowDuplicatesAndPrefersTheCleanerRow()
        {
            // Same ASIN, same author/title. Row A is 4 files, each chapter
            // imported twice in different naming styles (so really 2 unique
            // chapters). Row B is 2 files of the same chapters with no
            // intra-row duplication.
            //
            // The raw FileCount says A=4 > B=2, but the effective unique
            // count is the same (2 each); the cleaner row should win.
            var dirty = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Book", Asin = "B00DUPE0001",
                BasePath = "/audiobooks/Author/Book dirty",
            });
            foreach (var name in new[]
            {
                "01 Prologue_ Fortress of the Light.mp3",
                "01. Prologue -  Fortress of the Light.mp3",
                "02 Chapter 1_ Waiting.mp3",
                "02. Chapter 1 -  Waiting.mp3",
            })
            {
                await _audiobookFileRepository.AddAsync(new AudiobookFile
                {
                    AudiobookId = dirty.Id,
                    Path = $"/audiobooks/Author/Book dirty/{name}",
                    Bitrate = 128_000,
                    CreatedAt = DateTime.UtcNow,
                });
            }

            var clean = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Book", Asin = "B00DUPE0001",
                BasePath = "/audiobooks",
            });
            foreach (var name in new[]
            {
                "Book-01.mp3",
                "Book-02.mp3",
            })
            {
                await _audiobookFileRepository.AddAsync(new AudiobookFile
                {
                    AudiobookId = clean.Id,
                    Path = $"/audiobooks/{name}",
                    Bitrate = 128_000,
                    CreatedAt = DateTime.UtcNow,
                });
            }

            var controller = _provider.GetRequiredService<LibraryController>();
            var ok = await controller.GetDuplicates() as OkObjectResult;
            Assert.NotNull(ok);
            var payload = ok!.Value!;
            var groupsObj = payload.GetType().GetProperty("groups")!.GetValue(payload);
            var groupList = new List<DuplicateGroupDto>();
            foreach (var g in (System.Collections.IEnumerable)groupsObj!) groupList.Add((DuplicateGroupDto)g);

            Assert.Single(groupList);

            var dirtyDto = groupList[0].Rows.Single(r => r.Id == dirty.Id);
            var cleanDto = groupList[0].Rows.Single(r => r.Id == clean.Id);
            Assert.Equal(2, dirtyDto.LikelyDuplicateFileCount);
            Assert.Equal(0, cleanDto.LikelyDuplicateFileCount);

            var winner = groupList[0].Rows.Single(r => r.RecommendedWinner);
            Assert.Equal(clean.Id, winner.Id);
            Assert.Contains("Cleaner", groupList[0].RecommendationReason);
        }

        [Fact]
        public async Task GetDuplicates_PrefersHigherBitrateOverSingleFile()
        {
            // Same ASIN, both rows have files. One is a single low-bitrate
            // file; the other is multi-file at higher bitrate. Bitrate is
            // the dominant quality signal, so the multi-file high-bitrate
            // row should win.
            var lowBitrateSingle = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Book", Asin = "B00BITRATE01",
                BasePath = "/audiobooks/A/Book single",
                FilePath = "/audiobooks/A/Book single/Book.mp3",
            });
            await _audiobookFileRepository.AddAsync(new AudiobookFile
            {
                AudiobookId = lowBitrateSingle.Id,
                Path = "/audiobooks/A/Book single/Book.mp3",
                Bitrate = 32_000,
                CreatedAt = DateTime.UtcNow,
            });
            var highBitrateMulti = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Book", Asin = "B00BITRATE01",
                BasePath = "/audiobooks/A/Book chunked",
            });
            for (var i = 1; i <= 5; i++)
            {
                await _audiobookFileRepository.AddAsync(new AudiobookFile
                {
                    AudiobookId = highBitrateMulti.Id,
                    Path = $"/audiobooks/A/Book chunked/part-{i:00}.mp3",
                    Bitrate = 192_000,
                    CreatedAt = DateTime.UtcNow,
                });
            }

            var controller = _provider.GetRequiredService<LibraryController>();
            var ok = await controller.GetDuplicates() as OkObjectResult;
            Assert.NotNull(ok);
            var payload = ok!.Value!;
            var groupsObj = payload.GetType().GetProperty("groups")!.GetValue(payload);
            var groupList = new List<DuplicateGroupDto>();
            foreach (var g in (System.Collections.IEnumerable)groupsObj!) groupList.Add((DuplicateGroupDto)g);

            Assert.Single(groupList);
            var recommended = groupList[0].Rows.Single(r => r.RecommendedWinner);
            Assert.Equal(highBitrateMulti.Id, recommended.Id);
            Assert.Contains("bitrate", groupList[0].RecommendationReason, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task GetDuplicates_PrefersPathThatMatchesTitleAndAuthor()
        {
            // Same-ASIN trio where every row has 1 file, every row has a real
            // book folder — only differentiator is whether the BasePath
            // contains the actual book's title/author. The row whose folder
            // is "/audiobooks/A. American/Charlie's Requiem" should win over
            // the rows whose folders point at completely different books.
            var wrongDeanKoontz = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Charlie's Requiem: A Novella",
                Asin = "B00PATHM001",
                Authors = new List<string> { "A. American", "Walt Browning" },
                BasePath = "/audiobooks/Dean Koontz/A Big Little Life - A Memoir of a Joyful Dog",
                FilePath = "/audiobooks/Dean Koontz/A Big Little Life - A Memoir of a Joyful Dog/x.m4b",
            });
            await _audiobookFileRepository.AddAsync(new AudiobookFile
            {
                AudiobookId = wrongDeanKoontz.Id,
                Path = "/audiobooks/Dean Koontz/A Big Little Life - A Memoir of a Joyful Dog/x.m4b",
                CreatedAt = DateTime.UtcNow,
            });
            var wrongStephenKing = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Charlie's Requiem: A Novella",
                Asin = "B00PATHM001",
                Authors = new List<string> { "A. American", "Walt Browning" },
                BasePath = "/audiobooks/Stephen King/A Good Marriage",
                FilePath = "/audiobooks/Stephen King/A Good Marriage/y.m4b",
            });
            await _audiobookFileRepository.AddAsync(new AudiobookFile
            {
                AudiobookId = wrongStephenKing.Id,
                Path = "/audiobooks/Stephen King/A Good Marriage/y.m4b",
                CreatedAt = DateTime.UtcNow,
            });
            var correct = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Charlie's Requiem: A Novella",
                Asin = "B00PATHM001",
                Authors = new List<string> { "A. American", "Walt Browning" },
                BasePath = "/audiobooks/A. American/Charlie's Requiem - A Novella",
                FilePath = "/audiobooks/A. American/Charlie's Requiem - A Novella/z.m4b",
            });
            await _audiobookFileRepository.AddAsync(new AudiobookFile
            {
                AudiobookId = correct.Id,
                Path = "/audiobooks/A. American/Charlie's Requiem - A Novella/z.m4b",
                CreatedAt = DateTime.UtcNow,
            });

            var controller = _provider.GetRequiredService<LibraryController>();
            var ok = await controller.GetDuplicates() as OkObjectResult;
            Assert.NotNull(ok);
            var payload = ok!.Value!;
            var groupsObj = payload.GetType().GetProperty("groups")!.GetValue(payload);
            var groupList = new List<DuplicateGroupDto>();
            foreach (var g in (System.Collections.IEnumerable)groupsObj!) groupList.Add((DuplicateGroupDto)g);

            Assert.Single(groupList);
            var winner = groupList[0].Rows.Single(r => r.RecommendedWinner);
            Assert.Equal(correct.Id, winner.Id);
            Assert.Contains("Folder path matches", groupList[0].RecommendationReason);
        }

        [Fact]
        public async Task MergeDuplicates_DiscardRemovesFilesAndFolderFromDisk()
        {
            // Set up real on-disk files for a loser; after Discard, the
            // folder should be gone and the result should count what was
            // removed. Failure to touch disk surfaces as a warning, not an
            // error, but happy path is fully observable.
            var tempRoot = FileService.GetTempDirectory("listenarr-dedup-disk");
            var loserFolder = Path.Combine(tempRoot, "Author", "Loser Book");
            Directory.CreateDirectory(loserFolder);
            var loserFilePath = Path.Combine(loserFolder, "loser.mp3");
            await File.WriteAllTextAsync(loserFilePath, "fake mp3 data");

            // Pre-seed a root folder so the safety-net in
            // DeleteAudiobookFilesystemAsync recognizes loserFolder as
            // belonging to a configured root.
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithPath(tempRoot)
                .Build());

            var winner = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Winner", Asin = "B00DISK0001",
                BasePath = Path.Combine(tempRoot, "Author", "Winner Book"),
                FilePath = Path.Combine(tempRoot, "Author", "Winner Book", "w.m4b"),
            });
            var loser = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Loser", Asin = "B00DISK0001",
                BasePath = loserFolder,
                FilePath = loserFilePath,
            });
            await _audiobookFileRepository.AddAsync(new AudiobookFile
            {
                AudiobookId = loser.Id,
                Path = loserFilePath,
                CreatedAt = DateTime.UtcNow,
            });

            var controller = _provider.GetRequiredService<LibraryController>();
            var actionResult = await controller.MergeDuplicates(new MergeDuplicatesRequest
            {
                Merges =
                {
                    new MergePairDto
                    {
                        WinnerId = winner.Id,
                        LoserIds = new List<int> { loser.Id },
                    }
                }
            });

            var ok = actionResult as OkObjectResult;
            Assert.True(ok != null, $"Expected Ok, got {actionResult?.GetType().Name}: {(actionResult as ObjectResult)?.Value}");
            var result = (MergeDuplicatesResultDto)ok!.Value!;

            Assert.Equal(1, result.RowsDeleted);
            Assert.True(result.DiskFilesDeleted >= 1, $"Expected at least 1 file deleted from disk, got {result.DiskFilesDeleted}");
            Assert.True(result.DiskFoldersDeleted >= 1, $"Expected the loser's book folder to be deleted, got {result.DiskFoldersDeleted}");

            // Folder is gone on disk too.
            Assert.False(Directory.Exists(loserFolder), $"Expected {loserFolder} to be deleted but it still exists");
            Assert.False(File.Exists(loserFilePath));
        }

        [Fact]
        public async Task GetDuplicates_SurfacesFileMetadataAndRecommendationReason()
        {
            // Verify the extended response shape: per-row file list, totals,
            // narrators/authors, and a non-empty recommendation reason.
            var winner = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Book", Asin = "B00META0001",
                BasePath = "/audiobooks/Author/Book",
                FilePath = "/audiobooks/Author/Book/Book.mp3",
                Authors = new List<string> { "Some Author" },
                Narrators = new List<string> { "Some Narrator" },
                Runtime = 480,
            });
            await _audiobookFileRepository.AddAsync(new AudiobookFile
            {
                AudiobookId = winner.Id,
                Path = "/audiobooks/Author/Book/Book.mp3",
                Size = 12_345_678,
                Format = "mp3",
                Codec = "mp3",
                Bitrate = 128000,
                DurationSeconds = 28800,
                CreatedAt = DateTime.UtcNow,
            });
            await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Phantom", Asin = "B00META0001",
                BasePath = "/audiobooks",
            });

            var controller = _provider.GetRequiredService<LibraryController>();
            var ok = await controller.GetDuplicates() as OkObjectResult;
            Assert.NotNull(ok);
            var payload = ok!.Value!;
            var groupsObj = payload.GetType().GetProperty("groups")!.GetValue(payload);
            var groupList = new List<DuplicateGroupDto>();
            foreach (var g in (System.Collections.IEnumerable)groupsObj!) groupList.Add((DuplicateGroupDto)g);

            Assert.Single(groupList);
            var group = groupList[0];
            Assert.False(string.IsNullOrWhiteSpace(group.RecommendationReason),
                "Group should carry a human-readable recommendation reason");

            var winnerDto = group.Rows.Single(r => r.Id == winner.Id);
            Assert.True(winnerDto.RecommendedWinner);
            Assert.Single(winnerDto.Files);
            Assert.Equal(12_345_678, winnerDto.Files[0].Size);
            Assert.Equal(12_345_678, winnerDto.TotalSize);
            Assert.Contains("Some Author", winnerDto.Authors);
            Assert.Contains("Some Narrator", winnerDto.Narrators);
            Assert.Equal(480, winnerDto.Runtime);
        }

        // -------------------------------------------------------------------
        // Title/author dedup pass (Issue A) — rows that share a computed
        // canonical folder target via FolderNamingPattern but have distinct
        // ASINs. Catches edition variants and wrong-metadata rows the same-
        // ASIN pass can't see.
        // -------------------------------------------------------------------

        private async Task SeedFolderPatternAsync(string pattern = "{Author}/{Title}", string root = "/audiobooks")
        {
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithFolderNamingPattern(pattern)
                .WithOutputPath(root)
                .Build());
            await _rootFolderRepository.AddAsync(new RootFolderBuilder()
                .WithName("Library")
                .WithPath(root)
                .WithIsDefault()
                .Build());
        }

        private static List<DuplicateGroupDto> ExtractGroups(OkObjectResult ok)
        {
            var payload = ok!.Value!;
            var groupsObj = payload.GetType().GetProperty("groups")!.GetValue(payload);
            var groupList = new List<DuplicateGroupDto>();
            foreach (var g in (System.Collections.IEnumerable)groupsObj!) groupList.Add((DuplicateGroupDto)g);
            return groupList;
        }

        [Fact]
        public async Task GetDuplicates_TitleAuthorPass_GroupsRowsByComputedTarget()
        {
            await SeedFolderPatternAsync();

            // Two rows with the same {Author}/{Title} but distinct ASINs —
            // exactly the case the same-ASIN pass misses.
            await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Elantris",
                Authors = new List<string> { "Brandon Sanderson" },
                Asin = "B00EDIT0001",
                BasePath = "/audiobooks/Brandon Sanderson/Elantris (old)",
            });
            await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Elantris",
                Authors = new List<string> { "Brandon Sanderson" },
                Asin = "B00EDIT0002",
                BasePath = "/audiobooks/Brandon Sanderson/Elantris (new)",
            });
            // Unrelated row that should NOT collide with anything.
            await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Solo Book",
                Authors = new List<string> { "Solo Author" },
                Asin = "B00SOLO0001",
            });

            var controller = _provider.GetRequiredService<LibraryController>();
            var ok = await controller.GetDuplicates() as OkObjectResult;
            Assert.NotNull(ok);
            var groups = ExtractGroups(ok!);

            var titleGroup = Assert.Single(groups, g => g.Kind == DuplicateGroupKind.TitleAuthor);
            Assert.DoesNotContain(groups, g => g.Kind == DuplicateGroupKind.Asin);
            Assert.Equal(2, titleGroup.Rows.Count);
            Assert.False(string.IsNullOrEmpty(titleGroup.CollisionKey));
            Assert.Equal(string.Empty, titleGroup.NormalizedAsin);
            Assert.False(string.IsNullOrWhiteSpace(titleGroup.RecommendationReason));
        }

        [Fact]
        public async Task GetDuplicates_TitleAuthorPass_ExcludesRowsAlreadyInAsinGroup()
        {
            // Same-ASIN pair AND a third row sharing the same canonical
            // target but with a distinct ASIN. The third row alone has no
            // title/author collision partner outside the ASIN group, so the
            // title/author pass should emit NO group — sequential resolution:
            // the user merges the ASIN pair first, then a re-fetch may surface
            // a residual title/author collision against the survivor.
            await SeedFolderPatternAsync();

            await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Foundation",
                Authors = new List<string> { "Isaac Asimov" },
                Asin = "B00ASIN0001",
                BasePath = "/audiobooks/Isaac Asimov/Foundation",
            });
            await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Foundation",
                Authors = new List<string> { "Isaac Asimov" },
                Asin = "B00ASIN0001",
                BasePath = "/audiobooks/Isaac Asimov/Foundation (dupe)",
            });
            await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Foundation",
                Authors = new List<string> { "Isaac Asimov" },
                Asin = "B00ASIN0002",
                BasePath = "/audiobooks/Isaac Asimov/Foundation (variant)",
            });

            var controller = _provider.GetRequiredService<LibraryController>();
            var ok = await controller.GetDuplicates() as OkObjectResult;
            Assert.NotNull(ok);
            var groups = ExtractGroups(ok!);

            // One ASIN group (the B00ASIN0001 pair). No title/author group —
            // the third row's only would-be collision partners are inside the
            // ASIN group and are excluded from the title/author pass.
            var asinGroup = Assert.Single(groups, g => g.Kind == DuplicateGroupKind.Asin);
            Assert.Equal("B00ASIN0001", asinGroup.NormalizedAsin);
            Assert.DoesNotContain(groups, g => g.Kind == DuplicateGroupKind.TitleAuthor);
        }

        [Fact]
        public async Task GetDuplicates_TitleAuthorPass_DropsPhantomRowWhenAnyRowHasFiles()
        {
            // The "real + phantom" pattern: a row with files at the canonical
            // target plus a monitored-but-not-downloaded record (no
            // AudiobookFiles, no real BasePath) that resolves to the same
            // target. The phantom is noise — drop it. The group disappears
            // because only one row remains.
            await SeedFolderPatternAsync();

            var realBook = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "The Way of Kings",
                Authors = new List<string> { "Brandon Sanderson" },
                Asin = "B00REAL0001",
                BasePath = "/audiobooks/Brandon Sanderson/The Way of Kings",
                FilePath = "/audiobooks/Brandon Sanderson/The Way of Kings/The Way of Kings.m4b",
            });
            // Phantom: zero files, BasePath stamped at the root (the wishlist
            // pattern from the live library — not empty BasePath, just no
            // real book folder).
            await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "The Way of Kings",
                Authors = new List<string> { "Brandon Sanderson" },
                Asin = "B00WISH0001",
                BasePath = "/audiobooks",
            });
            // Attach a file to the real row so the file map distinguishes them.
            await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(realBook)
                .WithPath("/audiobooks/Brandon Sanderson/The Way of Kings/The Way of Kings.m4b")
                .Build());

            var controller = _provider.GetRequiredService<LibraryController>();
            var ok = await controller.GetDuplicates() as OkObjectResult;
            Assert.NotNull(ok);
            var groups = ExtractGroups(ok!);
            Assert.DoesNotContain(groups, g => g.Kind == DuplicateGroupKind.TitleAuthor);
        }

        [Fact]
        public async Task GetDuplicates_TitleAuthorPass_AllFilelessRowsStillSurfaceAsAGroup()
        {
            // The "phantom + phantom" pattern: two wishlist records for the
            // same book under distinct ASINs. The user needs to resolve which
            // ASIN to keep, so the group must surface (filter only drops the
            // phantom when a real-file row exists to make the choice trivial).
            await SeedFolderPatternAsync();
            await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "The Bands of Mourning",
                Authors = new List<string> { "Brandon Sanderson" },
                Asin = "B00WISH0001",
                BasePath = "/audiobooks",
            });
            await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "The Bands of Mourning",
                Authors = new List<string> { "Brandon Sanderson" },
                Asin = "B00WISH0002",
                BasePath = "/audiobooks",
            });

            var controller = _provider.GetRequiredService<LibraryController>();
            var ok = await controller.GetDuplicates() as OkObjectResult;
            Assert.NotNull(ok);
            var groups = ExtractGroups(ok!);

            var group = Assert.Single(groups, g => g.Kind == DuplicateGroupKind.TitleAuthor);
            Assert.Equal(2, group.Rows.Count);
            Assert.All(group.Rows, r => Assert.Equal(0, r.FileCount));
        }

        [Fact]
        public async Task GetDuplicates_TitleAuthorPass_RequiresAtLeastTwoRowsAtSameTarget()
        {
            // A row that computes a unique canonical target produces no
            // collision and therefore no group.
            await SeedFolderPatternAsync();
            await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Lone Book",
                Authors = new List<string> { "Lone Author" },
                Asin = "B00LONE0001",
            });

            var controller = _provider.GetRequiredService<LibraryController>();
            var ok = await controller.GetDuplicates() as OkObjectResult;
            Assert.NotNull(ok);
            Assert.Empty(ExtractGroups(ok!));
        }

        [Fact]
        public async Task MergeDuplicates_RejectsRowsThatDontShareAsin()
        {
            // Hardening check: even if a frontend forgets to suppress the merge
            // action for title/author groups and submits a cross-ASIN merge,
            // the server must reject the whole request.
            var winner = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Elantris",
                Authors = new List<string> { "Brandon Sanderson" },
                Asin = "B00EDIT0001",
            });
            var crossAsinLoser = await _audiobookRepository.AddAsync(new Audiobook
            {
                Title = "Elantris",
                Authors = new List<string> { "Brandon Sanderson" },
                Asin = "B00EDIT0002",
            });

            var controller = _provider.GetRequiredService<LibraryController>();
            var result = await controller.MergeDuplicates(new MergeDuplicatesRequest
            {
                Merges = new List<MergePairDto>
                {
                    new() { WinnerId = winner.Id, LoserIds = new List<int> { crossAsinLoser.Id } },
                },
            });

            Assert.IsType<BadRequestObjectResult>(result);
            // Confirm nothing was deleted.
            Assert.NotNull(await _audiobookRepository.GetByIdAsync(winner.Id));
            Assert.NotNull(await _audiobookRepository.GetByIdAsync(crossAsinLoser.Id));
        }
    }
}
