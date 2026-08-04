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
using Listenarr.Api.Features.Library;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Api.Features.Library
{
    [Trait("Area", "LibraryApi")]
    [Trait("Name", "LibraryController_DuplicateCopyApplyTests")]
    [Trait("Category", "LibraryController")]
    public class LibraryController_DuplicateCopyApplyTests : BaseTests
    {
        private static JsonElement ToJson(object? value) => JsonSerializer.SerializeToElement(value);

        /// <summary>
        /// Seeds a record whose folder holds the same book under two filename
        /// schemes. Corresponding files carry the given contents, so the
        /// sampled hash sees real bytes.
        /// </summary>
        private async Task<(Audiobook Book, List<AudiobookFile> SchemeA, List<AudiobookFile> SchemeB)>
            SeedTwoSchemesAsync(string folder, string[] contentsA, string[] contentsB)
        {
            var book = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Copied Book")
                .WithBasePath(FileService.GetTempDirectory(folder))
                .Build());
            Directory.CreateDirectory(book.BasePath!);

            async Task<List<AudiobookFile>> AddScheme(string stem, string[] contents)
            {
                var files = new List<AudiobookFile>();
                for (var i = 0; i < contents.Length; i++)
                {
                    var path = Path.Join(book.BasePath, $"{stem}-{i + 1:000}.mp3");
                    await File.WriteAllTextAsync(path, contents[i]);
                    files.Add(await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                        .WithAudiobook(book)
                        .WithPath(path)
                        .WithSize(contents[i].Length)
                        .WithDuration(2 * 3600)
                        .Build()));
                }
                return files;
            }

            var a = await AddScheme("Copied Book", contentsA);
            var b = await AddScheme("Same Story Rip", contentsB);
            return (book, a, b);
        }

        private static LibraryController.ApplyDuplicateCopiesRequest RequestFor(int bookId, IEnumerable<AudiobookFile> redundant) => new()
        {
            Applications =
            [
                new LibraryController.ApplyDuplicateCopyItem
                {
                    AudiobookId = bookId,
                    RedundantFileIds = redundant.Select(f => f.Id).ToList()
                }
            ]
        };

        [Fact]
        public async Task Apply_IdenticalCopies_DeletesRedundantClusterFromDiskAndDb()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var contents = new[] { new string('a', 400), new string('b', 500) };
            var (book, schemeA, schemeB) = await SeedTwoSchemesAsync("dupe-apply-identical", contents, contents);

            // The analysis proposes one keeper; apply whichever cluster it
            // marked redundant (bitrates tie, so resolve from the analysis).
            var analysis = _provider.GetRequiredService<DuplicateCopyProposalBuilder>()
                .Resolve(book, await _audiobookFileRepository.GetByAudiobookIdAsync(book.Id));
            var proposal = Assert.Single(analysis.Proposals);
            Assert.Equal("identical", proposal.Confidence);
            var redundantIds = proposal.Proposal.Redundant.SelectMany(c => c.FileIds).ToHashSet();
            var redundant = schemeA.Concat(schemeB).Where(f => redundantIds.Contains(f.Id)).ToList();
            var kept = schemeA.Concat(schemeB).Where(f => !redundantIds.Contains(f.Id)).ToList();

            var result = await controller.ApplyDuplicateCopies(
                RequestFor(book.Id, redundant), CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = ToJson(ok.Value);
            Assert.Equal(2, payload.GetProperty("totalFilesDeleted").GetInt32());

            foreach (var f in redundant)
            {
                Assert.Null(await _audiobookFileRepository.GetByIdAsync(f.Id));
                Assert.False(File.Exists(f.Path));
            }
            foreach (var f in kept)
            {
                Assert.NotNull(await _audiobookFileRepository.GetByIdAsync(f.Id));
                Assert.True(File.Exists(f.Path));
            }
        }

        [Fact]
        public async Task Apply_StaleFileSet_RefusesWithoutDeleting()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var contents = new[] { new string('a', 400), new string('b', 500) };
            var (book, schemeA, schemeB) = await SeedTwoSchemesAsync("dupe-apply-stale", contents, contents);

            // A file set that matches no current proposal (one file only).
            var result = await controller.ApplyDuplicateCopies(
                RequestFor(book.Id, schemeB.Take(1)), CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = ToJson(ok.Value);
            Assert.Equal(0, payload.GetProperty("totalFilesDeleted").GetInt32());
            var item = payload.GetProperty("results")[0];
            Assert.False(item.GetProperty("applied").GetBoolean());

            foreach (var f in schemeA.Concat(schemeB))
            {
                Assert.NotNull(await _audiobookFileRepository.GetByIdAsync(f.Id));
                Assert.True(File.Exists(f.Path));
            }
        }

        [Fact]
        public async Task Apply_ReviewTier_SizeMatchButContentDiffers_Refuses()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            // Same sizes, different bytes: analysis demotes to review — the
            // apply bar must refuse it.
            var (book, schemeA, schemeB) = await SeedTwoSchemesAsync(
                "dupe-apply-review",
                [new string('a', 400), new string('b', 500)],
                [new string('x', 400), new string('y', 500)]);

            var analysis = _provider.GetRequiredService<DuplicateCopyProposalBuilder>()
                .Resolve(book, await _audiobookFileRepository.GetByAudiobookIdAsync(book.Id));
            var proposal = Assert.Single(analysis.Proposals);
            Assert.Equal("review", proposal.Confidence);

            var result = await controller.ApplyDuplicateCopies(
                RequestFor(book.Id, schemeB), CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = ToJson(ok.Value);
            Assert.Equal(0, payload.GetProperty("totalFilesDeleted").GetInt32());

            foreach (var f in schemeA.Concat(schemeB))
            {
                Assert.True(File.Exists(f.Path));
            }
        }
    }
}
