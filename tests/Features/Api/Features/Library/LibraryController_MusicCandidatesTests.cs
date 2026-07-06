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
    [Trait("Name", "LibraryController_MusicCandidatesTests")]
    [Trait("Category", "LibraryController")]
    public class LibraryController_MusicCandidatesTests : BaseTests
    {
        private static JsonElement ToJson(object? value) => JsonSerializer.SerializeToElement(value);

        private async Task<Audiobook> AddBookAsync(
            string title,
            VerificationStatus status,
            int fileCount,
            double fileSeconds,
            string? transcript = null,
            string? detailJson = null)
        {
            var book = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle(title)
                .WithBasePath(FileService.GetTempDirectory($"music-{Guid.NewGuid():N}"))
                .Build());

            book.VerificationStatus = status;
            book.VerificationTranscript = transcript;
            book.VerificationDetailJson = detailJson;
            await _audiobookRepository.UpdateAsync(book);

            for (var i = 0; i < fileCount; i++)
            {
                var file = new AudiobookFileBuilder()
                    .WithAudiobook(book)
                    .WithPath(Path.Join(book.BasePath, $"track-{i:00}.mp3"))
                    .Build();
                file.DurationSeconds = fileSeconds;
                await _audiobookFileRepository.AddAsync(file);
            }

            return book;
        }

        [Fact]
        [Trait("Method", "GetMusicCandidates")]
        [Trait("Scenario", "FlagsAlbumShapedFlaggedBook_WithHeardCreditsFromDetailJson")]
        public async Task GetMusicCandidates_FlagsAlbumShapedFlaggedBook()
        {
            var controller = _provider.GetRequiredService<LibraryController>();

            // Album-shaped, flagged, transcript full of cues + performer ident,
            // heardCredits (camelCase, as the background service persists) naming
            // someone else.
            var album = await AddBookAsync(
                "Before Eden", VerificationStatus.AgentFlagged,
                fileCount: 12, fileSeconds: 185,
                transcript: "[Music] ♪ jumping jack flash ♪ [Music] performed by The Rolling Stones from the album (upbeat music)",
                detailJson: "{\"outcome\":\"mismatch\",\"heardCredits\":{\"author\":\"The Rolling Stones\"}}");

            // A chaptered audiobook, also flagged — must NOT appear.
            await AddBookAsync(
                "Real Audiobook", VerificationStatus.AgentFlagged,
                fileCount: 15, fileSeconds: 1500,
                transcript: "Chapter one. It was the best of times.");

            // Album-shaped but MATCH verdict — never a candidate.
            await AddBookAsync(
                "Verified Album-Shaped Collection", VerificationStatus.AgentVerified,
                fileCount: 12, fileSeconds: 185,
                transcript: "[Music] [Music] [Music]");

            // Album-shaped but manually rejected — human already ruled; excluded.
            await AddBookAsync(
                "Human Ruled", VerificationStatus.Rejected,
                fileCount: 12, fileSeconds: 185,
                transcript: "[Music] [Music] [Music]");

            var result = await controller.GetMusicCandidates(CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = ToJson(ok.Value);
            var candidates = payload.GetProperty("candidates").EnumerateArray().ToList();

            var row = Assert.Single(candidates);
            Assert.Equal(album.Id, row.GetProperty("id").GetInt32());
            Assert.True(row.GetProperty("score").GetDouble() >= 0.75);
            Assert.Equal(12, row.GetProperty("fileCount").GetInt32());
            Assert.Equal(185, row.GetProperty("medianDurationSeconds").GetDouble());
            Assert.Contains(row.GetProperty("reasons").EnumerateArray(),
                r => r.GetString()!.Contains("album shape"));
        }

        [Fact]
        [Trait("Method", "GetMusicCandidates")]
        [Trait("Scenario", "EmptyLibrary_ReturnsEmptyList")]
        public async Task GetMusicCandidates_EmptyLibrary_ReturnsEmptyList()
        {
            var controller = _provider.GetRequiredService<LibraryController>();

            var result = await controller.GetMusicCandidates(CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            Assert.Empty(ToJson(ok.Value).GetProperty("candidates").EnumerateArray());
        }

        [Fact]
        [Trait("Method", "ReadHeardCredits")]
        [Trait("Scenario", "TolerantOfMissingOrMalformedDetail")]
        public void ReadHeardCredits_TolerantOfMissingOrMalformedDetail()
        {
            Assert.Equal((null, null), LibraryMusicCandidatesWorkflow.ReadHeardCredits(null));
            Assert.Equal((null, null), LibraryMusicCandidatesWorkflow.ReadHeardCredits("not json {"));
            Assert.Equal((null, null), LibraryMusicCandidatesWorkflow.ReadHeardCredits("{\"outcome\":\"mismatch\"}"));

            var (title, author) = LibraryMusicCandidatesWorkflow.ReadHeardCredits(
                "{\"heardCredits\":{\"title\":\"Sticky Fingers\",\"author\":\"The Rolling Stones\"}}");
            Assert.Equal("Sticky Fingers", title);
            Assert.Equal("The Rolling Stones", author);
        }
    }
}
