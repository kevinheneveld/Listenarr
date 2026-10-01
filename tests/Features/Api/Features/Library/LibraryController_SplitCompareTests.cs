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
using Listenarr.Api.Features.Library;
using Microsoft.AspNetCore.Mvc;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Api.Features.Library
{
    [Trait("Area", "LibraryApi")]
    [Trait("Name", "LibraryController_SplitCompareTests")]
    [Trait("Category", "LibraryController")]
    public class LibraryController_SplitCompareTests : BaseTests
    {
        private LibrarySplitCompareWorkflow Workflow => _provider.GetRequiredService<LibrarySplitCompareWorkflow>();

        private static JsonElement ToJson(object value)
            => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        private async Task<(Audiobook book, List<AudiobookFile> files)> CreateBookAsync(
            string title, int fileCount, double hours, int kbps)
        {
            var folder = FileService.GetTempDirectory("split-compare");
            var book = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle(title)
                .WithBasePath(folder)
                .Build());

            var seconds = hours * 3600 / Math.Max(1, fileCount);
            var files = new List<AudiobookFile>();
            for (var i = 0; i < fileCount; i++)
            {
                var path = Path.Join(folder, $"{title}-{i + 1:D3}.mp3");
                await File.WriteAllTextAsync(path, "audio");
                files.Add(await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                    .WithAudiobook(book)
                    .WithPath(path)
                    .WithSize((long)(seconds * kbps * 1000 / 8))
                    .WithDuration(seconds)
                    .WithBitrate(kbps * 1000)
                    .Build()));
            }
            return (book, files);
        }

        [Fact]
        [Trait("Method", "CompareSplitGroup")]
        [Trait("Scenario", "HigherBitrateGroup_Better")]
        public async Task Compare_HigherBitrateGroup_IsBetter_AndListsTheExistingFiles()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var (pack, packFiles) = await CreateBookAsync("Series Pack", 12, 10, 128);
            var (target, targetFiles) = await CreateBookAsync("Book One", 4, 9.8, 64);

            var result = await controller.CompareSplitGroup(
                pack.Id,
                new LibrarySplitCompareWorkflow.CompareRequest
                {
                    TargetAudiobookId = target.Id,
                    FileIds = packFiles.Select(f => f.Id).ToList(),
                },
                Workflow,
                CancellationToken.None);

            var json = ToJson(Assert.IsType<OkObjectResult>(result).Value!);
            Assert.Equal("better", json.GetProperty("verdict").GetString());
            Assert.Equal(128, json.GetProperty("incoming").GetProperty("bitrateKbps").GetInt32());
            Assert.Equal(64, json.GetProperty("existing").GetProperty("bitrateKbps").GetInt32());
            Assert.Equal(4, json.GetProperty("existing").GetProperty("fileCount").GetInt32());
            Assert.Equal(
                targetFiles.Select(f => f.Id).OrderBy(i => i),
                json.GetProperty("existingFileIds").EnumerateArray().Select(e => e.GetInt32()).OrderBy(i => i));
        }

        [Fact]
        [Trait("Method", "CompareSplitGroup")]
        [Trait("Scenario", "OnlyTheGroupsFilesCount")]
        public async Task Compare_WeighsOnlyTheRequestedFiles()
        {
            // The group is a slice of the source record: the rest of the
            // pack's audio must not count toward the incoming copy's length.
            var controller = _provider.GetRequiredService<LibraryController>();
            var (pack, packFiles) = await CreateBookAsync("Series Pack", 12, 30, 128);
            var (target, _) = await CreateBookAsync("Book One", 4, 9.8, 64);

            var result = await controller.CompareSplitGroup(
                pack.Id,
                new LibrarySplitCompareWorkflow.CompareRequest
                {
                    TargetAudiobookId = target.Id,
                    FileIds = packFiles.Take(4).Select(f => f.Id).ToList(),
                },
                Workflow,
                CancellationToken.None);

            var json = ToJson(Assert.IsType<OkObjectResult>(result).Value!);
            Assert.Equal(4, json.GetProperty("incoming").GetProperty("fileCount").GetInt32());
            Assert.Equal(10 * 3600, json.GetProperty("incoming").GetProperty("totalDurationSeconds").GetDouble(), 0);
        }

        [Fact]
        [Trait("Method", "CompareSplitGroup")]
        [Trait("Scenario", "EmptyDestination")]
        public async Task Compare_EmptyDestination_ReportsEmpty()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var (pack, packFiles) = await CreateBookAsync("Series Pack", 6, 10, 128);
            var (target, _) = await CreateBookAsync("Book One", 0, 0, 64);

            var result = await controller.CompareSplitGroup(
                pack.Id,
                new LibrarySplitCompareWorkflow.CompareRequest
                {
                    TargetAudiobookId = target.Id,
                    FileIds = packFiles.Select(f => f.Id).ToList(),
                },
                Workflow,
                CancellationToken.None);

            var json = ToJson(Assert.IsType<OkObjectResult>(result).Value!);
            Assert.Equal("empty", json.GetProperty("verdict").GetString());
            Assert.Equal(0, json.GetProperty("existingFileIds").GetArrayLength());
        }

        [Fact]
        [Trait("Method", "CompareSplitGroup")]
        [Trait("Scenario", "ForeignFiles_BadRequest")]
        public async Task Compare_FilesNotOwnedBySource_BadRequest()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var (pack, _) = await CreateBookAsync("Series Pack", 3, 10, 128);
            var (target, targetFiles) = await CreateBookAsync("Book One", 3, 9.8, 64);

            var result = await controller.CompareSplitGroup(
                pack.Id,
                new LibrarySplitCompareWorkflow.CompareRequest
                {
                    TargetAudiobookId = target.Id,
                    FileIds = targetFiles.Select(f => f.Id).ToList(),
                },
                Workflow,
                CancellationToken.None);

            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Fact]
        [Trait("Method", "CompareSplitGroup")]
        [Trait("Scenario", "SameRecord_BadRequest")]
        public async Task Compare_DestinationIsTheSource_BadRequest()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var (pack, packFiles) = await CreateBookAsync("Series Pack", 3, 10, 128);

            var result = await controller.CompareSplitGroup(
                pack.Id,
                new LibrarySplitCompareWorkflow.CompareRequest
                {
                    TargetAudiobookId = pack.Id,
                    FileIds = packFiles.Select(f => f.Id).ToList(),
                },
                Workflow,
                CancellationToken.None);

            Assert.IsType<BadRequestObjectResult>(result);
        }
    }
}
