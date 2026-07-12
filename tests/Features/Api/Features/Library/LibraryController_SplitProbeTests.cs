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
using Listenarr.Tests.Mocks;

namespace Listenarr.Tests.Features.Api.Features.Library
{
    [Trait("Area", "LibraryApi")]
    [Trait("Name", "LibraryController_SplitProbeTests")]
    [Trait("Category", "LibraryController")]
    public class LibraryController_SplitProbeTests : BaseTests
    {
        private LibrarySplitProbeWorkflow Workflow => _provider.GetRequiredService<LibrarySplitProbeWorkflow>();

        private static JsonElement ToJson(object value)
            => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

        /// <summary>
        /// An anonymized collection: two same-encode books separated by a tiny
        /// intro stub — names carry nothing, shape carries everything.
        /// </summary>
        private async Task<(Audiobook book, List<AudiobookFile> files)> CreateCollectionAsync()
        {
            var folder = FileService.GetTempDirectory("probe-collection");
            var book = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Big Collection")
                .WithBasePath(folder)
                .Build());

            var shapes = new (double minutes, double mb)[]
            {
                (60, 27), (55, 25),      // book A
                (3.5, 1.7),              // intro stub
                (50, 23), (45, 20),      // book B
            };
            var files = new List<AudiobookFile>();
            for (var i = 0; i < shapes.Length; i++)
            {
                var path = Path.Join(folder, $"Collection-{i + 1:D3}.mp3");
                await File.WriteAllTextAsync(path, "audio");
                files.Add(await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                    .WithAudiobook(book)
                    .WithPath(path)
                    .WithSize((long)(shapes[i].mb * 1024 * 1024))
                    .WithDuration(shapes[i].minutes * 60)
                    .Build()));
            }
            return (book, files);
        }

        [Fact]
        [Trait("Method", "GetSplitProbeCandidates")]
        [Trait("Scenario", "ShapeDrivenCandidates")]
        public async Task Candidates_ReturnsStubAndNeighbors()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var (book, files) = await CreateCollectionAsync();

            var result = await controller.GetSplitProbeCandidates(book.Id, Workflow, CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var json = ToJson(ok.Value!);
            Assert.True(json.GetProperty("whisperAvailable").GetBoolean());
            Assert.Equal(5, json.GetProperty("totalFiles").GetInt32());

            var ids = json.GetProperty("candidates").EnumerateArray()
                .Select(c => c.GetProperty("fileId").GetInt32())
                .ToList();
            Assert.Contains(files[0].Id, ids); // first file
            Assert.Contains(files[2].Id, ids); // the stub
            Assert.Contains(files[3].Id, ids); // follows the stub
            Assert.DoesNotContain(files[1].Id, ids); // mid-book chapter
        }

        [Fact]
        [Trait("Method", "ProbeSplitBoundary")]
        [Trait("Scenario", "ForeignFile_BadRequest")]
        public async Task Probe_FileNotOwned_BadRequest()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var (book, _) = await CreateCollectionAsync();
            var (_, otherFiles) = await CreateCollectionAsync();

            var result = await controller.ProbeSplitBoundary(
                book.Id,
                new LibrarySplitProbeWorkflow.ProbeRequest { FileId = otherFiles[0].Id },
                Workflow,
                CancellationToken.None);

            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Fact]
        [Trait("Method", "ProbeSplitBoundary")]
        [Trait("Scenario", "WhisperUnavailable_BadRequest")]
        public async Task Probe_WhisperUnavailable_BadRequest()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var (book, files) = await CreateCollectionAsync();
            var whisper = (WhisperServiceMock)_provider
                .GetRequiredService<Listenarr.Application.Audiobooks.Verification.Contracts.IWhisperService>();
            whisper.Available = false;
            try
            {
                var result = await controller.ProbeSplitBoundary(
                    book.Id,
                    new LibrarySplitProbeWorkflow.ProbeRequest { FileId = files[0].Id },
                    Workflow,
                    CancellationToken.None);

                Assert.IsType<BadRequestObjectResult>(result);
            }
            finally
            {
                whisper.Available = true;
            }
        }

        [Fact]
        [Trait("Method", "PlanSplitFromProbes")]
        [Trait("Scenario", "TranscriptsBecomeLabeledGroups")]
        public async Task Plan_BuildsGroupsAndSuggestsExistingRecords()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var (book, files) = await CreateCollectionAsync();

            // An existing same-author record the announced title should match.
            var existing = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Escaping Home")
                .WithBasePath(FileService.GetTempDirectory("probe-existing"))
                .Build());
            existing.Authors = book.Authors;
            await _audiobookRepository.UpdateAsync(existing);

            var request = new LibrarySplitProbeWorkflow.ProbePlanRequest
            {
                Probes = new List<LibrarySplitProbeWorkflow.ProbePlanEntry>
                {
                    new() { FileId = files[0].Id, Transcript = "This is Audible. Chapter one, in ordinary prose." },
                    new() { FileId = files[2].Id, Transcript = "Penguin Audio presents Escaping Home by A. American, read by Duke Fontaine." },
                }
            };

            var result = await controller.PlanSplitFromProbes(book.Id, request, Workflow, CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var json = ToJson(ok.Value!);
            var clusters = json.GetProperty("clusters").EnumerateArray().ToList();
            Assert.Equal(2, clusters.Count);

            // Group 1: files 1-2, unlabeled retail opener.
            Assert.Equal(2, clusters[0].GetProperty("fileIds").GetArrayLength());

            // Group 2: opens at the announced stub, labeled, suggesting the
            // existing record.
            var second = clusters[1];
            Assert.Equal(3, second.GetProperty("fileIds").GetArrayLength());
            Assert.Equal("Escaping Home", second.GetProperty("label").GetString());
            Assert.Equal(existing.Id, second.GetProperty("suggestedTargetId").GetInt32());
            Assert.Equal("audio-probe", second.GetProperty("suggestionSource").GetString());
            Assert.False(string.IsNullOrEmpty(second.GetProperty("boundaryTranscript").GetString()));
        }

        [Fact]
        [Trait("Method", "PlanSplitFromProbes")]
        [Trait("Scenario", "NoProbes_BadRequest")]
        public async Task Plan_WithoutProbes_BadRequest()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var (book, _) = await CreateCollectionAsync();

            var result = await controller.PlanSplitFromProbes(
                book.Id, new LibrarySplitProbeWorkflow.ProbePlanRequest(), Workflow, CancellationToken.None);

            Assert.IsType<BadRequestObjectResult>(result);
        }
    }
}
