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

using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Infrastructure.Metadata
{
    /// <summary>
    /// A file whose metadata cannot be extracted keeps matching the missing-metadata
    /// query; the processor must back off instead of re-probing it every cycle.
    /// </summary>
    [Trait("Name", "MetadataRescanBackoffTests")]
    [Trait("Category", "Metadata")]
    public class MetadataRescanBackoffTests : BaseTests
    {
        [Fact]
        public void BackoffDelay_DoublesFromTenMinutes_AndCapsAtOneDay()
        {
            Assert.Equal(TimeSpan.FromMinutes(10), MetadataRescanProcessor.BackoffDelay(1));
            Assert.Equal(TimeSpan.FromMinutes(20), MetadataRescanProcessor.BackoffDelay(2));
            Assert.Equal(TimeSpan.FromMinutes(40), MetadataRescanProcessor.BackoffDelay(3));
            Assert.Equal(TimeSpan.FromHours(24), MetadataRescanProcessor.BackoffDelay(20));
        }

        [Fact]
        public async Task RunCycle_FileThatStaysMetadataLess_IsNotReprobedNextCycle()
        {
            var file = new AudiobookFile { Id = 77, AudiobookId = 9, Path = "/library/book/part1.mp3" };
            var fileRepository = new Mock<IAudiobookFileRepository>();
            fileRepository
                .Setup(r => r.GetMissingMetadataAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([file]);
            fileRepository
                .Setup(r => r.GetByIdAsync(file.Id, It.IsAny<CancellationToken>()))
                .ReturnsAsync(file);
            var audiobookRepository = new Mock<IAudiobookRepository>();
            // Audiobook gone → the rescan bails out early; the file still lacks metadata.
            audiobookRepository
                .Setup(r => r.GetForScanSnapshotAsync(file.AudiobookId, It.IsAny<CancellationToken>()))
                .ReturnsAsync((Audiobook?)null);
            var services = new ServiceCollection();
            services.AddSingleton(fileRepository.Object);
            services.AddSingleton(audiobookRepository.Object);
            services.AddSingleton(Mock.Of<IAudiobookFilePathIdentityResolver>());
            services.AddSingleton(Mock.Of<IAudiobookFileService>());
            using var provider = services.BuildServiceProvider();
            using var operationCoordinator = new AudiobookOperationCoordinator();
            var moveQueueService = new Mock<IMoveQueueService>();
            moveQueueService
                .Setup(s => s.GetRecoveryStateForAudiobookAsync(It.IsAny<int>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(MoveRecoveryState.None);
            var processor = new MetadataRescanProcessor(
                provider.GetRequiredService<IServiceScopeFactory>(),
                operationCoordinator,
                moveQueueService.Object,
                Mock.Of<ILogger<MetadataRescanProcessor>>());

            await processor.RunCycleAsync(CancellationToken.None);
            await processor.RunCycleAsync(CancellationToken.None);
            await processor.RunCycleAsync(CancellationToken.None);

            fileRepository.Verify(r => r.GetByIdAsync(file.Id, It.IsAny<CancellationToken>()), Times.Once);
            // The second cycle looked one entry further so the backed-off file can't pin the batch.
            fileRepository.Verify(r => r.GetMissingMetadataAsync(21, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        }
    }
}
