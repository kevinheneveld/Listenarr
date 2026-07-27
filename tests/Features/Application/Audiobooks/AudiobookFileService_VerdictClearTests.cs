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
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Audiobooks
{
    [Trait("Area", "Application")]
    [Trait("Name", "AudiobookFileService_VerdictClearTests")]
    public class AudiobookFileService_VerdictClearTests : BaseTests
    {
        private async Task<(Audiobook audiobook, List<AudiobookFile> files)> CreateFlaggedAudiobookAsync(
            string folderName,
            VerificationStatus status,
            int fileCount)
        {
            var folder = FileService.GetTempDirectory(folderName);
            var audiobook = new AudiobookBuilder()
                .WithTitle("Destination: Void")
                .WithBasePath(folder)
                .Build();
            audiobook.VerificationStatus = status;
            audiobook.VerificationConfidence = 0.38;
            audiobook.VerifiedAt = DateTime.UtcNow;
            audiobook.VerificationMethod = "agent:whisper-base.en";
            audiobook = await _audiobookRepository.AddAsync(audiobook);

            var files = new List<AudiobookFile>();
            for (var i = 0; i < fileCount; i++)
            {
                var path = Path.Join(folder, $"part{i + 1}.mp3");
                await File.WriteAllTextAsync(path, "audio");
                files.Add(await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                    .WithAudiobook(audiobook)
                    .WithPath(path)
                    .Build()));
            }

            return (audiobook, files);
        }

        [Fact]
        [Trait("Scenario", "AgentVerdictClearedWhenLastFileRemoved")]
        public async Task Delete_ClearsAgentVerdict_WhenLastFileRemoved()
        {
            var service = _provider.GetRequiredService<IAudiobookFileService>();
            var (audiobook, files) = await CreateFlaggedAudiobookAsync(
                "verdict-clear-last", VerificationStatus.AgentFlagged, 2);

            await service.DeleteAudiobookFileAsync(audiobook, files[0].Id, deleteFromDisk: false);
            var afterFirst = await _audiobookRepository.GetByIdAsync(audiobook.Id);
            // One file still remains — the verdict still describes real audio.
            Assert.Equal(VerificationStatus.AgentFlagged, afterFirst!.VerificationStatus);

            await service.DeleteAudiobookFileAsync(afterFirst, files[1].Id, deleteFromDisk: false);
            var afterLast = await _audiobookRepository.GetByIdAsync(audiobook.Id);
            Assert.Equal(VerificationStatus.Unverified, afterLast!.VerificationStatus);
            Assert.Null(afterLast.VerificationConfidence);
            Assert.Null(afterLast.VerifiedAt);
            Assert.Null(afterLast.VerificationMethod);
        }

        [Fact]
        [Trait("Scenario", "ManualVerdictSurvivesLastFileRemoval")]
        public async Task Delete_KeepsManualVerdict_WhenLastFileRemoved()
        {
            var service = _provider.GetRequiredService<IAudiobookFileService>();
            var (audiobook, files) = await CreateFlaggedAudiobookAsync(
                "verdict-clear-manual", VerificationStatus.ManuallyVerified, 1);

            await service.DeleteAudiobookFileAsync(audiobook, files[0].Id, deleteFromDisk: false);

            var after = await _audiobookRepository.GetByIdAsync(audiobook.Id);
            // Manual states are sticky by design — file removal must not undo
            // a human ruling (the wrong-content flow relies on the same rule
            // for Rejected).
            Assert.Equal(VerificationStatus.ManuallyVerified, after!.VerificationStatus);
        }
    }
}
