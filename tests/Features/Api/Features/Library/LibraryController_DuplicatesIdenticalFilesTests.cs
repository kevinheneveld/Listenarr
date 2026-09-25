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
    /// <summary>
    /// The identical-files pass of the duplicates sweep: two records whose
    /// tracked files carry the same size multiset are the same audio no
    /// matter what their titles or ASINs say. This is the case the ASIN-strict
    /// passes deliberately skip (two distinct ASINs = two editions), which is
    /// how a library ends up with several records over one copied rip.
    /// </summary>
    [Trait("Area", "LibraryApi")]
    [Trait("Name", "LibraryController_DuplicatesIdenticalFilesTests")]
    [Trait("Category", "LibraryController")]
    public class LibraryController_DuplicatesIdenticalFilesTests : BaseTests
    {
        private const long Mb = 1024L * 1024;

        private static JsonElement ToJson(object? value) => JsonSerializer.SerializeToElement(value);

        private async Task<Audiobook> AddBookAsync(string title, string? asin, params long?[] sizes)
        {
            var book = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle(title)
                .WithAuthor("Some Author")
                .WithBasePath(FileService.GetTempDirectory($"dupes-{Guid.NewGuid():N}"))
                .Build());
            book.Asin = asin;
            await _audiobookRepository.UpdateAsync(book);

            for (var i = 0; i < sizes.Length; i++)
            {
                var file = new AudiobookFileBuilder()
                    .WithAudiobook(book)
                    .WithPath(Path.Join(book.BasePath, $"part-{i:00}.mp3"))
                    .Build();
                file.Size = sizes[i];
                await _audiobookFileRepository.AddAsync(file);
            }

            return book;
        }

        private async Task<List<JsonElement>> GetGroupsAsync(string reason)
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var result = Assert.IsType<OkObjectResult>(await controller.GetDuplicates(CancellationToken.None));
            var payload = ToJson(result.Value);
            return payload.GetProperty("duplicateGroups")
                .EnumerateArray()
                .Where(g => g.GetProperty("reason").GetString() == reason)
                .ToList();
        }

        private static List<int> BookIds(JsonElement group) => group
            .GetProperty("books")
            .EnumerateArray()
            .Select(b => b.GetProperty("id").GetInt32())
            .OrderBy(id => id)
            .ToList();

        [Fact]
        [Trait("Method", "GetDuplicates")]
        [Trait("Scenario", "GroupsRecordsWithIdenticalFileSets_AcrossDifferentTitlesAndAsins")]
        public async Task GetDuplicates_GroupsIdenticalFileSets_AcrossDifferentTitlesAndAsins()
        {
            // Same 3-file rip tracked by two records with unrelated titles and
            // two distinct ASINs — invisible to the asin and title-author passes.
            var original = await AddBookAsync("The Hard Way", "B00LI7VIRS", 120 * Mb, 118 * Mb, 121 * Mb);
            var copy = await AddBookAsync("The Hard Way (Unabridged)", "B002V1LRZS", 121 * Mb, 120 * Mb, 118 * Mb);

            // Same count, different sizes — a different rip, never grouped.
            await AddBookAsync("The Hard Way", "B004PKD9MM", 120 * Mb, 118 * Mb, 122 * Mb);

            var groups = await GetGroupsAsync("identical-files");

            var group = Assert.Single(groups);
            Assert.Equal(new List<int> { original.Id, copy.Id }.OrderBy(id => id).ToList(), BookIds(group));
            Assert.StartsWith("identical-files:", group.GetProperty("key").GetString());
            Assert.Equal(Math.Min(original.Id, copy.Id), group.GetProperty("suggestedKeeperId").GetInt32());
        }

        [Fact]
        [Trait("Method", "GetDuplicates")]
        [Trait("Scenario", "IgnoresSmallSetsAndUnknownSizes")]
        public async Task GetDuplicates_IgnoresSmallSetsAndUnknownSizes()
        {
            // Below the book-sized floor: two 5 MB stubs agreeing to the byte
            // is a coincidence, not a duplicate.
            await AddBookAsync("Stub A", "AAAA000001", 5 * Mb);
            await AddBookAsync("Stub B", "AAAA000002", 5 * Mb);

            // A record with an unknown size never participates, even when its
            // known files match another record exactly.
            await AddBookAsync("Partly Known", "AAAA000003", 300 * Mb, null);
            await AddBookAsync("Partly Known Twin", "AAAA000004", 300 * Mb, null);

            var groups = await GetGroupsAsync("identical-files");

            Assert.Empty(groups);
        }

        [Fact]
        [Trait("Method", "GetDuplicates")]
        [Trait("Scenario", "AsinGroupsTakePrecedence_NoDoubleListing")]
        public async Task GetDuplicates_AsinGroupsTakePrecedence()
        {
            // Same ASIN and identical files: reported once, as an asin group.
            var a = await AddBookAsync("Dune", "B002V57VRC", 400 * Mb);
            var b = await AddBookAsync("Dune", "B002V57VRC", 400 * Mb);

            var asinGroups = await GetGroupsAsync("asin");
            var identicalGroups = await GetGroupsAsync("identical-files");

            var asinGroup = Assert.Single(asinGroups);
            Assert.Equal(new List<int> { a.Id, b.Id }.OrderBy(id => id).ToList(), BookIds(asinGroup));
            Assert.Empty(identicalGroups);
        }
    }
}
