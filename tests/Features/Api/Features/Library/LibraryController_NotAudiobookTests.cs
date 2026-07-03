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
    [Trait("Name", "LibraryController_NotAudiobookTests")]
    [Trait("Category", "LibraryController")]
    public class LibraryController_NotAudiobookTests : BaseTests
    {
        private static JsonElement ToJson(object? value) => JsonSerializer.SerializeToElement(value);

        [Fact]
        [Trait("Scenario", "ReturnsNotFound_WhenAudiobookMissing")]
        public async Task RejectNotAudiobook_ReturnsNotFound_WhenAudiobookMissing()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var result = await controller.RejectNotAudiobook(999_301, CancellationToken.None);
            Assert.IsType<NotFoundObjectResult>(result);
        }

        [Fact]
        [Trait("Scenario", "RemovesFiles_ResetsVerdict_KeepsMonitored_BlocklistsDeliveredRelease")]
        public async Task RejectNotAudiobook_RemovesFiles_ResetsVerdict_KeepsMonitored_BlocklistsDeliveredRelease()
        {
            var controller = _provider.GetRequiredService<LibraryController>();
            var folder = FileService.GetTempDirectory("not-audiobook");
            var ab = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Titans")
                .WithBasePath(folder)
                .WithMonitored(false)
                .Build());

            // Wrong content on disk + a flagged verdict describing it.
            var path = Path.Join(folder, "junk.mp3");
            await File.WriteAllTextAsync(path, "music");
            await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(ab).WithPath(path).Build());
            ab.VerificationStatus = VerificationStatus.AgentFlagged;
            ab.VerificationConfidence = 0.95;
            ab.VerificationTranscript = "some transcript";
            await _audiobookRepository.UpdateAsync(ab);

            // The download that delivered it.
            var delivered = new DownloadBuilder()
                .WithStatus(DownloadStatus.Completed)
                .Build();
            delivered.AudiobookId = ab.Id;
            delivered.Title = "VA-Clash_of_the_Titans-SINGLE-WEB-2026";
            await _downloadRepository.AddAsync(delivered);

            var result = await controller.RejectNotAudiobook(ab.Id, CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = ToJson(ok.Value);
            Assert.Equal(1, payload.GetProperty("filesRemoved").GetInt32());

            // Files gone from DB and disk.
            Assert.Empty(await _audiobookFileRepository.GetByAudiobookIdAsync(ab.Id));
            Assert.False(File.Exists(path));

            // Verdict reset, book re-monitored.
            var reloaded = await _audiobookRepository.GetByIdAsync(ab.Id);
            Assert.Equal(VerificationStatus.Unverified, reloaded!.VerificationStatus);
            Assert.Null(reloaded.VerificationConfidence);
            Assert.Null(reloaded.VerificationTranscript);
            Assert.True(reloaded.Monitored);

            // The delivering release is blocklisted.
            var blockedRepo = _provider.GetRequiredService<IBlockedReleaseRepository>();
            var blocked = await blockedRepo.GetByAudiobookIdAsync(ab.Id);
            var entry = Assert.Single(blocked);
            Assert.Equal("VA-Clash_of_the_Titans-SINGLE-WEB-2026", entry.ReleaseTitle);

            // History recorded.
            var history = await _historyRepository.GetByAudiobookIdAsync(ab.Id);
            Assert.Contains(history, h => h.EventType == "Rejected");
        }
    }
}
