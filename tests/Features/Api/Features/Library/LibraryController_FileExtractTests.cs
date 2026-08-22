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
using Listenarr.Application.Audiobooks.Files;
using Microsoft.AspNetCore.Mvc;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Api.Features.Library
{
    [Trait("Area", "LibraryApi")]
    [Trait("Name", "LibraryController_FileExtractTests")]
    [Trait("Category", "LibraryController")]
    public class LibraryController_FileExtractTests : BaseTests
    {
        private async Task<(Audiobook source, AudiobookFile file)> CreateSourceWithFileAsync(string folder = "extract-src", string fileName = "wrong-book.m4b")
        {
            // #717: destination mutations require the path inside an authorized root.
            await AddAuthorizedRootAsync(FileService.GetTempPath());
            var sourceFolder = FileService.GetTempDirectory(folder);
            var source = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Anthology Record")
                .WithBasePath(sourceFolder)
                .Build());

            var path = Path.Join(sourceFolder, fileName);
            await File.WriteAllTextAsync(path, "audio");
            var file = await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(source)
                .WithPath(path)
                .Build());
            return (source, file);
        }

        private static ExtractFileRequest RequestFor(string title, string? asin = null, DuplicateStrategy strategy = DuplicateStrategy.None)
        {
            return new ExtractFileRequest
            {
                Metadata = new AudibleBookMetadata { Title = title, Asin = asin, Authors = new List<string> { "Test Author" } },
                DuplicateStrategy = strategy,
                Monitored = false,
            };
        }

        [Fact]
        [Trait("Method", "ExtractFileToNewAudiobook")]
        [Trait("Scenario", "HappyPath_CreatesRecordAndMovesFile")]
        public async Task Extract_CreatesNewRecord_ReassignsAndMovesFile()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var (source, file) = await CreateSourceWithFileAsync();

            var result = await controller.ExtractFileToNewAudiobook(
                source.Id, file.Id, RequestFor("The Real Book"), CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = Assert.IsType<ExtractFileResult>(ok.Value);
            Assert.True(payload.Success);
            Assert.NotNull(payload.DestinationAudiobookId);
            Assert.True(payload.SourceAudiobookEmpty);

            // File row reassigned to the new record with the new absolute path.
            var moved = await _audiobookFileRepository.GetByIdAsync(file.Id);
            Assert.NotNull(moved);
            Assert.Equal(payload.DestinationAudiobookId, moved!.AudiobookId);
            Assert.False(string.IsNullOrWhiteSpace(moved.Path));

            // Physically moved: old path gone, new path exists.
            Assert.False(File.Exists(file.Path));
            Assert.True(File.Exists(moved.Path));

            // Destination record exists with the chosen title.
            var destination = await _audiobookRepository.GetByIdAsync(payload.DestinationAudiobookId!.Value);
            Assert.NotNull(destination);
            Assert.Equal("The Real Book", destination!.Title);

            // History on both records.
            var sourceHistory = await _historyRepository.GetByAudiobookIdAsync(source.Id);
            var destHistory = await _historyRepository.GetByAudiobookIdAsync(destination.Id);
            Assert.Contains(sourceHistory, h => h.EventType == "File Removed" && h.Source == "FileExtraction");
            Assert.Contains(destHistory, h => h.EventType == "File Added" && h.Source == "FileExtraction");
        }

        [Fact]
        [Trait("Method", "ExtractFileToNewAudiobook")]
        [Trait("Scenario", "WrongOwner_Fails")]
        public async Task Extract_FileNotOwnedBySource_Fails()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var (_, file) = await CreateSourceWithFileAsync();
            var other = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Unrelated")
                .WithBasePath(FileService.GetTempDirectory("extract-other"))
                .Build());

            var result = await controller.ExtractFileToNewAudiobook(
                other.Id, file.Id, RequestFor("Whatever"), CancellationToken.None);

            var bad = Assert.IsType<BadRequestObjectResult>(result);
            var payload = Assert.IsType<ExtractFileResult>(bad.Value);
            Assert.False(payload.Success);
            Assert.Contains("does not belong", payload.Error);
        }

        [Fact]
        [Trait("Method", "ExtractFileToNewAudiobook")]
        [Trait("Scenario", "AsinConflict_Returns409_ThenMergeSucceeds")]
        public async Task Extract_AsinConflict_ThenMerge()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var (source, file) = await CreateSourceWithFileAsync();

            // A different record already owns the chosen ASIN.
            var existing = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("The Real Book")
                .WithBasePath(Path.Join(FileService.GetTempPath(), "extract-existing"))
                .Build());
            existing.Asin = "B000EXTRACT1";
            await _audiobookRepository.UpdateAsync(existing);

            // No strategy → 409 with conflict details.
            var conflictResult = await controller.ExtractFileToNewAudiobook(
                source.Id, file.Id, RequestFor("The Real Book", asin: "B000EXTRACT1"), CancellationToken.None);
            var conflict = Assert.IsType<ConflictObjectResult>(conflictResult);
            var conflictPayload = Assert.IsType<ExtractFileResult>(conflict.Value);
            Assert.NotNull(conflictPayload.Conflict);
            Assert.Equal(existing.Id, conflictPayload.Conflict!.ExistingAudiobookId);

            // Merge strategy → file lands on the existing record.
            var mergeResult = await controller.ExtractFileToNewAudiobook(
                source.Id, file.Id, RequestFor("The Real Book", asin: "B000EXTRACT1", strategy: DuplicateStrategy.Merge), CancellationToken.None);
            var ok = Assert.IsType<OkObjectResult>(mergeResult);
            var payload = Assert.IsType<ExtractFileResult>(ok.Value);
            Assert.True(payload.Success);
            Assert.Equal(existing.Id, payload.DestinationAudiobookId);
            Assert.Equal(DuplicateStrategy.Merge, payload.AppliedStrategy);

            var moved = await _audiobookFileRepository.GetByIdAsync(file.Id);
            Assert.Equal(existing.Id, moved!.AudiobookId);
        }

        [Fact]
        [Trait("Method", "ExtractFileToNewAudiobook")]
        [Trait("Scenario", "DuplicateStrategy_BypassesAddDedup")]
        public async Task Extract_DuplicateStrategy_CreatesSecondRecord()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var (source, file) = await CreateSourceWithFileAsync();

            var existing = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("The Real Book")
                .WithBasePath(Path.Join(FileService.GetTempPath(), "extract-existing-dup"))
                .Build());
            existing.Asin = "B000EXTRACT2";
            await _audiobookRepository.UpdateAsync(existing);

            var result = await controller.ExtractFileToNewAudiobook(
                source.Id, file.Id, RequestFor("The Real Book", asin: "B000EXTRACT2", strategy: DuplicateStrategy.Duplicate), CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = Assert.IsType<ExtractFileResult>(ok.Value);
            Assert.True(payload.Success);
            Assert.NotNull(payload.DestinationAudiobookId);
            Assert.NotEqual(existing.Id, payload.DestinationAudiobookId);
            Assert.Equal(DuplicateStrategy.Duplicate, payload.AppliedStrategy);

            // The duplicate must NOT carry the contested ASIN: the DB's
            // partial unique index (absent from this InMemory test double,
            // which is how the original regression slipped through) rejects a
            // second row with the same ASIN outright. The existing record
            // keeps it; the duplicate coexists ASIN-less as a title+author
            // duplicate group.
            var duplicate = await _audiobookRepository.GetByIdAsync(payload.DestinationAudiobookId!.Value);
            Assert.NotNull(duplicate);
            Assert.True(string.IsNullOrEmpty(duplicate!.Asin));
            var reloadedExisting = await _audiobookRepository.GetByIdAsync(existing.Id);
            Assert.Equal("B000EXTRACT2", reloadedExisting!.Asin);
        }

        [Fact]
        [Trait("Method", "ExtractFileToNewAudiobook")]
        [Trait("Scenario", "MissingTitle_BadRequest")]
        public async Task Extract_MissingTitle_BadRequest()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var (source, file) = await CreateSourceWithFileAsync();

            var result = await controller.ExtractFileToNewAudiobook(
                source.Id, file.Id, new ExtractFileRequest { Metadata = new AudibleBookMetadata() }, CancellationToken.None);

            Assert.IsType<BadRequestObjectResult>(result);
        }
    }
}
