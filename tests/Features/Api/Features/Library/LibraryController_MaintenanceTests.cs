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
    [Trait("Name", "LibraryController_MaintenanceTests")]
    [Trait("Category", "LibraryController")]
    public class LibraryController_MaintenanceTests : BaseTests
    {
        private static JsonElement ToJson(object? value) => JsonSerializer.SerializeToElement(value);

        private async Task<Audiobook> AddBookAsync(string title, string? asin, string? folder = null, int files = 0)
        {
            // #717: destination mutations require the path inside an authorized root.
            await AddAuthorizedRootAsync(FileService.GetTempPath());
            var basePath = folder != null ? FileService.GetTempDirectory(folder) : FileService.GetTempPath();
            var builder = new AudiobookBuilder().WithTitle(title).WithAuthor("Test Author").WithBasePath(basePath);
            var book = builder.Build();
            book.Asin = asin;
            var added = await _audiobookRepository.AddAsync(book);
            for (var i = 0; i < files; i++)
            {
                var path = Path.Join(basePath, $"part{i + 1}.m4b");
                Directory.CreateDirectory(basePath);
                await File.WriteAllTextAsync(path, "audio");
                // #717: destructive filesystem cleanup requires rows carrying both a
                // resolved path identity and a proven physical generation.
                var identityResolver = _provider.GetRequiredService<IAudiobookFilePathIdentityResolver>();
                var fileRow = new AudiobookFileBuilder().WithAudiobook(added).WithPath(path).Build();
                fileRow.ApplyPathIdentity(path, await identityResolver.ResolveAsync(added, path));
                using (var parent = Listenarr.Infrastructure.FileSystem.PinnedDirectoryCreation.OpenPinnedHierarchyNoFollow(basePath, createMissing: false))
                using (var pinned = parent.OpenExistingFileForStableRead(Path.GetFileName(path)))
                {
                    fileRow.ApplyPhysicalObjectIdentity(pinned.GetObjectIdentity(), DateTime.UtcNow);
                }
                await _audiobookFileRepository.AddAsync(fileRow);
            }
            return added;
        }

        [Fact]
        [Trait("Method", "MergeDuplicates")]
        [Trait("Scenario", "MergesLoserIntoWinner_ReassignsAndDeletes")]
        public async Task MergeDuplicates_MergesLoserIntoWinner()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var winner = await AddBookAsync("Dup Book", "B000MERGE1", "merge-winner", files: 2);
            var loser = await AddBookAsync("Dup Book", "B000MERGE1", "merge-loser", files: 1);
            await _historyRepository.AddAsync(new History
            {
                AudiobookId = loser.Id,
                AudiobookTitle = loser.Title,
                EventType = "Added",
                Source = "test",
                Timestamp = DateTime.UtcNow,
            });

            var request = new LibraryController.MergeDuplicatesRequest
            {
                Merges = [new LibraryController.MergeDuplicatesPair { WinnerId = winner.Id, LoserIds = [loser.Id], ClearAsinIds = [] }],
            };
            var result = await controller.MergeDuplicates(request, CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = ToJson(ok.Value);
            Assert.Equal(1, payload.GetProperty("RowsDeleted").GetInt32());
            Assert.Equal(1, payload.GetProperty("HistoryReassigned").GetInt32());

            Assert.Null(await _audiobookRepository.GetByIdAsync(loser.Id));
            Assert.NotNull(await _audiobookRepository.GetByIdAsync(winner.Id));
            var history = await _historyRepository.GetByAudiobookIdAsync(winner.Id);
            Assert.Contains(history, h => h.EventType == "Added");
        }

        [Fact]
        [Trait("Method", "MergeDuplicates")]
        [Trait("Scenario", "RefusesAsinMismatch")]
        public async Task MergeDuplicates_RefusesAsinMismatch()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var winner = await AddBookAsync("Book A", "B000AAA111", "mismatch-w");
            var other = await AddBookAsync("Book B", "B000BBB222", "mismatch-l");

            var request = new LibraryController.MergeDuplicatesRequest
            {
                Merges = [new LibraryController.MergeDuplicatesPair { WinnerId = winner.Id, LoserIds = [other.Id], ClearAsinIds = [] }],
            };
            var result = await controller.MergeDuplicates(request, CancellationToken.None);

            Assert.IsType<BadRequestObjectResult>(result);
            Assert.NotNull(await _audiobookRepository.GetByIdAsync(other.Id));
        }

        [Fact]
        [Trait("Method", "MergeDuplicates")]
        [Trait("Scenario", "ClearsAsins_KeepsRows")]
        public async Task MergeDuplicates_ClearsAsins_KeepsRows()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var keeper = await AddBookAsync("Same ASIN A", "B000CLEAR1", "clear-a");
            var different = await AddBookAsync("Same ASIN B", "B000CLEAR1", "clear-b");

            var request = new LibraryController.MergeDuplicatesRequest
            {
                Merges = [new LibraryController.MergeDuplicatesPair { WinnerId = keeper.Id, LoserIds = [], ClearAsinIds = [different.Id] }],
            };
            var result = await controller.MergeDuplicates(request, CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Equal(1, ToJson(ok.Value).GetProperty("AsinsCleared").GetInt32());

            var reloaded = await _audiobookRepository.GetByIdAsync(different.Id);
            Assert.NotNull(reloaded);
            Assert.True(string.IsNullOrEmpty(reloaded!.Asin));
            var keeperReloaded = await _audiobookRepository.GetByIdAsync(keeper.Id);
            Assert.Equal("B000CLEAR1", keeperReloaded!.Asin);
        }

        [Fact]
        [Trait("Method", "ResolveAsinConflict")]
        [Trait("Scenario", "KeepThisRecord_MergesConflictingIntoIt")]
        public async Task ResolveAsinConflict_KeepThisRecord_MergesConflictingIntoIt()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            // Unlike MergeDuplicates, the two rows do NOT share an ASIN here —
            // that's the whole point: this endpoint exists because assigning
            // recordId this ASIN collided with conflictingId, which already has it.
            var record = await AddBookAsync("The Ring", null, "conflict-record", files: 1);
            var conflicting = await AddBookAsync("The Shattering Peace", "B000CONFLICT", "conflict-other", files: 1);
            await _historyRepository.AddAsync(new History
            {
                AudiobookId = conflicting.Id,
                AudiobookTitle = conflicting.Title,
                EventType = "Added",
                Source = "test",
                Timestamp = DateTime.UtcNow,
            });

            var result = await controller.ResolveAsinConflict(
                record.Id,
                new LibraryController.ResolveAsinConflictRequest { ConflictingAudiobookId = conflicting.Id, KeepThisRecord = true },
                CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = Assert.IsType<LibraryController.ResolveAsinConflictResult>(ok.Value);
            Assert.Equal(record.Id, payload.WinnerId);
            Assert.Equal(conflicting.Id, payload.LoserId);
            Assert.Equal(1, payload.DiskFilesDeleted);

            Assert.NotNull(await _audiobookRepository.GetByIdAsync(record.Id));
            Assert.Null(await _audiobookRepository.GetByIdAsync(conflicting.Id));
            var history = await _historyRepository.GetByAudiobookIdAsync(record.Id);
            Assert.Contains(history, h => h.EventType == "Added");
        }

        [Fact]
        [Trait("Method", "ResolveAsinConflict")]
        [Trait("Scenario", "KeepOtherRecord_MergesThisIntoConflicting")]
        public async Task ResolveAsinConflict_KeepOtherRecord_MergesThisIntoConflicting()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var record = await AddBookAsync("The Ring", null, "conflict-record-2", files: 1);
            var conflicting = await AddBookAsync("The Shattering Peace", "B000CONFLICT2", "conflict-other-2", files: 1);

            var result = await controller.ResolveAsinConflict(
                record.Id,
                new LibraryController.ResolveAsinConflictRequest { ConflictingAudiobookId = conflicting.Id, KeepThisRecord = false },
                CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = Assert.IsType<LibraryController.ResolveAsinConflictResult>(ok.Value);
            Assert.Equal(conflicting.Id, payload.WinnerId);
            Assert.Equal(record.Id, payload.LoserId);

            Assert.Null(await _audiobookRepository.GetByIdAsync(record.Id));
            Assert.NotNull(await _audiobookRepository.GetByIdAsync(conflicting.Id));
        }

        [Fact]
        [Trait("Method", "CleanupPhantomRows")]
        [Trait("Scenario", "DryRunPlansMerge_ThenApplies")]
        public async Task CleanupPhantomRows_MergesZeroFilePhantomIntoFileOwner()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var owner = await AddBookAsync("Phantom Target", "B000PHANT1", "phantom-owner", files: 1);
            var phantom = await AddBookAsync("Phantom Target", "B000PHANT1", "phantom-ghost");

            var dry = await controller.CleanupPhantomRows(dryRun: true, CancellationToken.None);
            var dryPayload = ToJson(Assert.IsType<OkObjectResult>(dry).Value);
            Assert.Equal(1, dryPayload.GetProperty("merges").GetInt32());
            Assert.NotNull(await _audiobookRepository.GetByIdAsync(phantom.Id));

            var applied = await controller.CleanupPhantomRows(dryRun: false, CancellationToken.None);
            var appliedPayload = ToJson(Assert.IsType<OkObjectResult>(applied).Value);
            Assert.Equal(1, appliedPayload.GetProperty("rowsDeleted").GetInt32());
            Assert.Null(await _audiobookRepository.GetByIdAsync(phantom.Id));
            Assert.NotNull(await _audiobookRepository.GetByIdAsync(owner.Id));
        }

        [Fact]
        [Trait("Method", "RecoverOrphanedTracking")]
        [Trait("Scenario", "EnqueuesScanForFolderWithAudioButNoTrackedFiles")]
        public async Task RecoverOrphanedTracking_FindsOrphanedRow()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            // Folder with audio on disk but zero tracked files.
            var folder = FileService.GetTempDirectory("orphaned-tracking");
            await File.WriteAllTextAsync(Path.Join(folder, "book.m4b"), "audio");
            var book = new AudiobookBuilder().WithTitle("Orphaned").WithAuthor("Author").WithBasePath(folder).Build();
            await _audiobookRepository.AddAsync(book);

            var result = await controller.RecoverOrphanedTracking(dryRun: true, CancellationToken.None);
            var payload = ToJson(Assert.IsType<OkObjectResult>(result).Value);
            Assert.Equal(1, payload.GetProperty("recovered").GetInt32());
        }

        [Fact]
        [Trait("Method", "CleanupOrphanMoveTmp")]
        [Trait("Scenario", "FindsAndDeletesStagingDirs")]
        public async Task CleanupOrphanMoveTmp_FindsAndDeletesStagingDirs()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var root = FileService.GetTempDirectory("orphan-tmp-root");
            await _rootFolderRepository.AddAsync(new RootFolderBuilder().WithPath(root).Build());

            var orphan = Path.Join(root, "Author", "Book.tmp-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(orphan);
            await File.WriteAllTextAsync(Path.Join(orphan, "part1.m4b"), "junk");

            var dry = await controller.CleanupOrphanMoveTmp(dryRun: true, CancellationToken.None);
            var dryPayload = ToJson(Assert.IsType<OkObjectResult>(dry).Value);
            Assert.Equal(1, dryPayload.GetProperty("found").GetInt32());
            Assert.True(Directory.Exists(orphan));

            var applied = await controller.CleanupOrphanMoveTmp(dryRun: false, CancellationToken.None);
            var appliedPayload = ToJson(Assert.IsType<OkObjectResult>(applied).Value);
            Assert.Equal(1, appliedPayload.GetProperty("deleted").GetInt32());
            Assert.False(Directory.Exists(orphan));
        }
    }
}
