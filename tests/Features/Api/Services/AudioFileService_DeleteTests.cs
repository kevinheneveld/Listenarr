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
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Moq;
using Listenarr.Domain.Models;
using Listenarr.Application.Audiobooks;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Metadata;
using Listenarr.Tests.Common;
using Listenarr.Tests.Builders;

namespace Listenarr.Tests.Features.Api.Services
{
    public class AudioFileService_DeleteTests : BaseTests
    {
        private Audiobook _audiobook = new AudiobookBuilder()
            .WithTitle("Delete-target book")
            .WithAuthor("Random guy")
            .Build();

        public override async Task InitializeAsync()
        {
            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.IsAny<string>()))
                .ReturnsAsync(new AudioMetadata { Duration = TimeSpan.FromSeconds(1234), Format = "m4b", BitRate = 64000, SampleRate = 32000, Channels = 1 });

            _services.AddSingleton(metadataMock.Object);
            Init();
            await _audiobookRepository.AddAsync(_audiobook);
        }

        private async Task<(AudiobookFile file, string path)> SeedFileAsync(Audiobook owner)
        {
            var path = Path.Join(Path.GetTempPath(), $"afs-delete-{Guid.NewGuid()}.m4b");
            await File.WriteAllTextAsync(path, "dummy");

            var svc = _provider.GetRequiredService<IAudiobookFileService>();
            var created = await svc.EnsureAudiobookFileAsync(owner, path, "test-seed");
            Assert.True(created);

            var file = (await _audiobookFileRepository.GetByAudiobookIdAsync(owner.Id)).First(f => f.Path == path);
            return (file, path);
        }

        [Fact]
        public async Task DeleteAudiobookFileAsync_FileNotFound_ReturnsNotFound()
        {
            var svc = _provider.GetRequiredService<IAudiobookFileService>();
            var result = await svc.DeleteAudiobookFileAsync(_audiobook, fileId: 99_999, deleteFromDisk: false);

            Assert.Equal(DeleteAudiobookFileOutcome.NotFound, result.Outcome);
            Assert.False(result.DeletedFromDisk);
            Assert.Empty(result.Warnings);
        }

        [Fact]
        public async Task DeleteAudiobookFileAsync_BelongsToDifferentAudiobook_ReturnsWrongAudiobookOutcome()
        {
            var other = new AudiobookBuilder().WithTitle("Other book").WithAuthor("Other").Build();
            await _audiobookRepository.AddAsync(other);
            var (file, path) = await SeedFileAsync(other);

            try
            {
                var svc = _provider.GetRequiredService<IAudiobookFileService>();
                var result = await svc.DeleteAudiobookFileAsync(_audiobook, file.Id, deleteFromDisk: true);

                Assert.Equal(DeleteAudiobookFileOutcome.DoesNotBelongToAudiobook, result.Outcome);
                Assert.False(result.DeletedFromDisk);
                Assert.True(File.Exists(path), "File on disk must not be touched when ownership check fails");

                var remaining = await _audiobookFileRepository.GetByIdAsync(file.Id);
                Assert.NotNull(remaining);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public async Task DeleteAudiobookFileAsync_DbOnly_RemovesRowAndLeavesDiskFile()
        {
            var (file, path) = await SeedFileAsync(_audiobook);

            try
            {
                var svc = _provider.GetRequiredService<IAudiobookFileService>();
                var result = await svc.DeleteAudiobookFileAsync(_audiobook, file.Id, deleteFromDisk: false);

                Assert.Equal(DeleteAudiobookFileOutcome.Deleted, result.Outcome);
                Assert.False(result.DeletedFromDisk);
                Assert.Empty(result.Warnings);
                Assert.Equal(path, result.Path);

                Assert.Null(await _audiobookFileRepository.GetByIdAsync(file.Id));
                Assert.True(File.Exists(path), "File should remain on disk when deleteFromDisk=false");
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public async Task DeleteAudiobookFileAsync_DeletesFromDisk_RemovesBoth()
        {
            var (file, path) = await SeedFileAsync(_audiobook);

            var svc = _provider.GetRequiredService<IAudiobookFileService>();
            var result = await svc.DeleteAudiobookFileAsync(_audiobook, file.Id, deleteFromDisk: true);

            Assert.Equal(DeleteAudiobookFileOutcome.Deleted, result.Outcome);
            Assert.True(result.DeletedFromDisk);
            Assert.Empty(result.Warnings);
            Assert.Null(await _audiobookFileRepository.GetByIdAsync(file.Id));
            Assert.False(File.Exists(path));
        }

        [Fact]
        public async Task DeleteAudiobookFileAsync_WritesFileRemovedHistory()
        {
            var (file, path) = await SeedFileAsync(_audiobook);

            try
            {
                var svc = _provider.GetRequiredService<IAudiobookFileService>();
                var result = await svc.DeleteAudiobookFileAsync(_audiobook, file.Id, deleteFromDisk: false, source: "manual");
                Assert.Equal(DeleteAudiobookFileOutcome.Deleted, result.Outcome);

                var histories = await _historyRepository.GetByAudiobookIdAsync(_audiobook.Id);
                var removal = histories.First(h => h.EventType == "File Removed");
                Assert.Contains(Path.GetFileName(path), removal.Message);
                Assert.Equal("manual", removal.Source);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public async Task DeleteAudiobookFileAsync_DiskMissing_SucceedsWithoutWarning()
        {
            var (file, path) = await SeedFileAsync(_audiobook);
            File.Delete(path);
            Assert.False(File.Exists(path));

            var svc = _provider.GetRequiredService<IAudiobookFileService>();
            var result = await svc.DeleteAudiobookFileAsync(_audiobook, file.Id, deleteFromDisk: true);

            Assert.Equal(DeleteAudiobookFileOutcome.Deleted, result.Outcome);
            Assert.False(result.DeletedFromDisk);
            Assert.Empty(result.Warnings);
            Assert.Null(await _audiobookFileRepository.GetByIdAsync(file.Id));
        }
    }
}
