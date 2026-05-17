using Xunit;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Listenarr.Domain.Models;
using Listenarr.Application.Interfaces;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Api.Services
{
    public class AudioFileService_ForceMetadataRefreshTests : BaseTests
    {
        [Fact]
        public async Task EnsureAudiobookFileAsync_ForceMetadataRefresh_PromotesBlankFieldsOnAlreadyTrackedFile()
        {
            var refreshedMeta = new AudioMetadata
            {
                Title = "From File",
                AlbumArtist = "Brandon Sanderson",
                Series = "Mistborn",
                Asin = "B002UZHDC0",
                Format = "m4b",
                Duration = TimeSpan.FromSeconds(1),
                BitRate = 64000,
                SampleRate = 44100,
                Channels = 2,
            };
            var metadataMock = new Mock<IMetadataService>();
            metadataMock.Setup(m => m.ExtractFileMetadataAsync(It.IsAny<string>())).ReturnsAsync(refreshedMeta);
            _services.AddSingleton(metadataMock.Object);
            Init();

            var book = new Audiobook { Title = "Existing Title", Monitored = true };
            await _audiobookRepository.AddAsync(book);

            var tempFile = await FileService.GetTempFileAsync($"refresh-{Guid.NewGuid()}.m4b");

            // Pre-register the file (simulating an already-scanned library entry).
            await _audiobookFileRepository.AddAsync(new AudiobookFile
            {
                AudiobookId = book.Id,
                Path = tempFile,
                Format = "m4b",
                Bitrate = 64000,
                Source = "scan",
            });

            var audiobookFileService = _provider.GetRequiredService<IAudiobookFileService>();

            var created = await audiobookFileService.EnsureAudiobookFileAsync(book, tempFile, "force-refresh", forceMetadataRefresh: true);
            Assert.False(created); // File already tracked — no new record.

            var after = await _audiobookRepository.GetByIdAsync(book.Id);
            Assert.NotNull(after);

            // Title was already set; not overwritten.
            Assert.Equal("Existing Title", after!.Title);
            // Blank fields filled from tags.
            Assert.NotNull(after.Authors);
            Assert.Equal("Brandon Sanderson", after.Authors![0]);
            Assert.Equal("Mistborn", after.Series);
            Assert.Equal("B002UZHDC0", after.Asin);
        }

        [Fact]
        public async Task EnsureAudiobookFileAsync_WithoutForceRefresh_DoesNotPromoteForExistingFile()
        {
            var metadataMock = new Mock<IMetadataService>(MockBehavior.Strict); // strict: must not be called
            _services.AddSingleton(metadataMock.Object);
            Init();

            var book = new Audiobook { Title = "Existing Title", Monitored = true };
            await _audiobookRepository.AddAsync(book);

            var tempFile = await FileService.GetTempFileAsync($"norefresh-{Guid.NewGuid()}.m4b");

            await _audiobookFileRepository.AddAsync(new AudiobookFile
            {
                AudiobookId = book.Id,
                Path = tempFile,
                Format = "m4b",
                Source = "scan",
            });

            var audiobookFileService = _provider.GetRequiredService<IAudiobookFileService>();

            var created = await audiobookFileService.EnsureAudiobookFileAsync(book, tempFile, "scan", forceMetadataRefresh: false);

            Assert.False(created);
            metadataMock.VerifyNoOtherCalls();
        }
    }
}
