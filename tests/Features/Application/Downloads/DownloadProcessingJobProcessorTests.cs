using Listenarr.Domain.Models;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Downloads;
using Moq;
using Listenarr.Tests.Mocks;

namespace Listenarr.Tests.Features.Application.Downloads
{
    [Trait("Name", "DownloadProcessingJobProcessorTests")]
    [Trait("Category", "DownloadProcessingJob")]
    public class DownloadProcessingJobProcessorTests : BaseTests
    {
        private readonly Mock<IDownloadImportService> downloadImportServiceMock = new();
        private readonly DownloadClientGatewayMock downloadClientGatewayMock = new();

        public override async Task InitializeAsync()
        {
            _services.AddSingleton<IDownloadClientGateway>(downloadClientGatewayMock);
            Init();
        }

        [Fact]
        public async Task CompletedDownload_With_NoPathFails()
        {
            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithAudiobook(await CreateAudiobook())
                .WithDownloadClientConfiguration(await CreateDownloadClientConfiguration())
                .WithPath("")
                .WithCompletedStatus(at: DateTime.UtcNow)
                .Build());

            var job = await _downloadProcessingJobRepository.AddAsync(new DownloadProcessingJobBuilder()
                .WithDownload(download)
                .Build());

            var downloadProcessingJobProcessor = _provider.GetRequiredService<DownloadProcessingJobProcessor>();
            await downloadProcessingJobProcessor.ProcessQueueAsync(CancellationToken.None);

            downloadImportServiceMock.Verify(m => m.ImportDownloadFilesAsync(
                    It.IsAny<Audiobook>(),
                    It.IsAny<List<string>>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);

            job = await _downloadProcessingJobRepository.GetByIdAsync(job.Id);
            Assert.NotNull(job);
            Assert.Equal(ProcessingJobStatus.Failed, job.Status);
        }

        [Theory]
        [InlineData("directoryMissing", false)]
        [InlineData("directoryExists", true)]
        public async Task CompletedDownload_With_MissingSource(string path, bool pathExists)
        {
            var sourceDirectory = FileService.GetTempDirectory("source-directory");
            path = Path.Join(sourceDirectory, path);
            if (pathExists)
            {
                Directory.CreateDirectory(path);
            }

            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithAudiobook(await CreateAudiobook())
                .WithDownloadClientConfiguration(await CreateDownloadClientConfiguration())
                .WithPath(path)
                .WithCompletedStatus(at: DateTime.UtcNow)
                .Build());

            var job = await _downloadProcessingJobRepository.AddAsync(new DownloadProcessingJobBuilder()
                .WithDownload(download)
                .Build());

            // First try
            var downloadProcessingJobProcessor = _provider.GetRequiredService<DownloadProcessingJobProcessor>();
            await downloadProcessingJobProcessor.ProcessQueueAsync(CancellationToken.None);

            downloadImportServiceMock.Verify(m => m.ImportDownloadFilesAsync(
                    It.IsAny<Audiobook>(),
                    It.IsAny<List<string>>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);

            job = await _downloadProcessingJobRepository.GetByIdAsync(job.Id);
            Assert.NotNull(job);
            Assert.Equal(ProcessingJobStatus.Pending, job.Status);
            Assert.Equal(1, job.RetryCount);
            Assert.NotEmpty(job.ErrorMessage);

            download = await _downloadRepository.FindAsync(download.Id);
            Assert.NotNull(download);
            Assert.Equal(DownloadStatus.ImportPending, download.Status);

            job.RetryCount = job.MaxRetries;
            await TestUtils.CancelJobRetryWait(_downloadProcessingJobRepository, job);

            // Last try
            await downloadProcessingJobProcessor.ProcessQueueAsync(CancellationToken.None);

            job = await _downloadProcessingJobRepository.GetByIdAsync(job.Id);
            Assert.NotNull(job);
            Assert.Equal(ProcessingJobStatus.Failed, job.Status);

            download = await _downloadRepository.FindAsync(download.Id);
            Assert.NotNull(download);
            Assert.Equal(DownloadStatus.ImportBlocked, download.Status);
            Assert.NotNull(download.ImportBlockMessages);
            Assert.Contains(download.ImportBlockMessages, m => m.Contains("see the log of job", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task Import_SingleFile_UpdatesStatus()
        {
            // Arrange
            var source = FileService.GetTempDirectory("source");
            var filePath = await FileService.GetFileAsync(source, "audiobook.mp3");

            downloadClientGatewayMock.SourceFiles = [filePath];

            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithCompletedStatus(at: DateTime.UtcNow)
                .WithDownloadClientConfiguration(await CreateDownloadClientConfiguration())
                .WithAudiobook(await CreateAudiobook())
                .WithPath(source)
                .Build());

            await _downloadProcessingJobRepository.AddAsync(new DownloadProcessingJobBuilder()
                .WithDownload(download)
                .Build());

            // Act
            var processor = _provider.GetRequiredService<DownloadProcessingJobProcessor>();
            await processor.ProcessQueueAsync(CancellationToken.None);

            // Assert
            download = await _downloadRepository.GetByIdAsync(download.Id);
            Assert.NotNull(download);
            Assert.True(download.Status == DownloadStatus.Moved, $"Expected Moved, got {download.Status}");
        }

        [Fact]
        public async Task Import_MultipleFiles_UpdatesStatus()
        {
            // Arrange
            var source = FileService.GetTempDirectory("source");
            var filePath1 = await FileService.GetFileAsync(source, "audiobook1.mp3");
            var filePath2 = await FileService.GetFileAsync(source, "audiobook2.mp3");
            var filePath3 = await FileService.GetFileAsync(source, "audiobook3.mp3");

            downloadClientGatewayMock.SourceFiles = [
                filePath1,
                filePath2,
                filePath3
            ];

            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithCompletedStatus(at: DateTime.UtcNow)
                .WithDownloadClientConfiguration(await CreateDownloadClientConfiguration())
                .WithAudiobook(await CreateAudiobook())
                .WithPath(source)
                .Build());

            await _downloadProcessingJobRepository.AddAsync(new DownloadProcessingJobBuilder()
                .WithDownload(download)
                .Build());

            // Act
            var processor = _provider.GetRequiredService<DownloadProcessingJobProcessor>();
            await processor.ProcessQueueAsync(CancellationToken.None);

            // Assert
            download = await _downloadRepository.GetByIdAsync(download.Id);
            Assert.NotNull(download);
            Assert.True(download.Status == DownloadStatus.Moved, $"Expected Moved, got {download.Status}");
        }

        [Fact]
        public async Task Import_OnlyRelevantFiles()
        {
            var basePath = FileService.GetTempDirectory("destination");
            var sourcePath = FileService.GetTempDirectory("downloads");
            var targetAudioPath = await FileService.GetFileAsync(sourcePath, "Target Book.m4b");
            var coverPath = await FileService.GetFileAsync(sourcePath, "cover.jpg");
            await FileService.GetFileAsync(sourcePath, "Different Book.m4b");

            downloadClientGatewayMock.SourceFiles = [
                targetAudioPath,
                coverPath
            ];

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithBasePath(basePath)
                .Build());

            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithAudiobook(audiobook)
                .WithDownloadClientConfiguration(await CreateDownloadClientConfiguration())
                .WithCompletedStatus(at: DateTime.UtcNow)
                .WithPath(sourcePath)
                .Build());

            await _downloadProcessingJobRepository.AddAsync(new DownloadProcessingJobBuilder()
                .WithDownload(download)
                .Build());

            // Act
            var processor = _provider.GetRequiredService<DownloadProcessingJobProcessor>();
            await processor.ProcessQueueAsync(CancellationToken.None);

            Assert.True(File.Exists(Path.Join(basePath, "Target Book.m4b")));
            Assert.True(File.Exists(Path.Join(basePath, "cover.jpg")));
            Assert.False(File.Exists(Path.Join(basePath, "Different Book.m4b")));
        }

        [Fact]
        public async Task CompletedDownload_FilesOwnedByAnotherAudiobook_FailsWithCollisionMessage()
        {
            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithMoveFileOnCompleted()
                .WithoutMetadataProcessing()
                .Build());

            var basePath = FileService.GetTempDirectory("collision-dest");
            var ownedPath = Path.Join(basePath, "Shared Title.m4b");

            // Owner audiobook already has this file registered and present on disk.
            var owner = await _audiobookRepository.AddAsync(new AudiobookBuilder().WithBasePath(basePath).Build());
            await File.WriteAllTextAsync(ownedPath, "OWNER");
            await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                .WithAudiobook(owner)
                .WithPath(ownedPath)
                .Build());

            // A different audiobook's completed download resolves to the same destination path.
            var other = await _audiobookRepository.AddAsync(new AudiobookBuilder().WithBasePath(basePath).Build());
            var sourcePath = FileService.GetTempDirectory("downloads");
            var sourceFile = await FileService.GetFileAsync(sourcePath, "Shared Title.m4b");

            downloadClientGatewayMock.SourceFiles = [sourceFile];

            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithAudiobook(other)
                .WithDownloadClientConfiguration(await CreateDownloadClientConfiguration())
                .WithCompletedStatus(at: DateTime.UtcNow)
                .WithPath(sourcePath)
                .Build());

            var job = await _downloadProcessingJobRepository.AddAsync(new DownloadProcessingJobBuilder()
                .WithDownload(download)
                .Build());

            // Act
            var processor = _provider.GetRequiredService<DownloadProcessingJobProcessor>();
            await processor.ProcessQueueAsync(CancellationToken.None);

            // Owner's file is untouched and the job fails with the collision-specific reason.
            Assert.Equal("OWNER", await File.ReadAllTextAsync(ownedPath));
            job = await _downloadProcessingJobRepository.GetByIdAsync(job.Id);
            Assert.NotNull(job);
            Assert.Equal(ProcessingJobStatus.Failed, job.Status);
            Assert.NotNull(job.ErrorMessage);
            Assert.Contains("already belong to another audiobook", job.ErrorMessage);
        }

        [Fact]
        public async Task RetryJob_IsNotProcessedBeforeTheRetryTimerExpires()
        {
            var sourceDirectory = FileService.GetTempDirectory("source-directory");
            var path = Path.Join(sourceDirectory, "missing");

            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithAudiobook(await CreateAudiobook())
                .WithDownloadClientConfiguration(await CreateDownloadClientConfiguration())
                .WithPath(path)
                .WithCompletedStatus(at: DateTime.UtcNow)
                .Build());

            var job = await _downloadProcessingJobRepository.AddAsync(new DownloadProcessingJobBuilder()
                .WithDownload(download)
                .Build());

            // First try: Should trigger a retry
            var downloadProcessingJobProcessor = _provider.GetRequiredService<DownloadProcessingJobProcessor>();
            await downloadProcessingJobProcessor.ProcessQueueAsync(CancellationToken.None);

            downloadImportServiceMock.Verify(m => m.ImportDownloadFilesAsync(
                    It.IsAny<Audiobook>(),
                    It.IsAny<List<string>>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);

            job = await _downloadProcessingJobRepository.GetByIdAsync(job.Id);
            Assert.NotNull(job);
            Assert.Equal(ProcessingJobStatus.Pending, job.Status);
            Assert.Equal(1, job.RetryCount);
            Assert.NotEmpty(job.ErrorMessage);

            download = await _downloadRepository.FindAsync(download.Id);
            Assert.NotNull(download);
            Assert.Equal(DownloadStatus.ImportPending, download.Status);

            // Retry immediately
            await downloadProcessingJobProcessor.ProcessQueueAsync(CancellationToken.None);

            // Job is not modified
            job = await _downloadProcessingJobRepository.GetByIdAsync(job.Id);
            Assert.NotNull(job);
            Assert.Equal(ProcessingJobStatus.Pending, job.Status);
            Assert.Equal(1, job.RetryCount);

            // Retry after timer expires
            await TestUtils.CancelJobRetryWait(_downloadProcessingJobRepository, job);
            await downloadProcessingJobProcessor.ProcessQueueAsync(CancellationToken.None);

            // Job is retried (and refailed)
            job = await _downloadProcessingJobRepository.GetByIdAsync(job.Id);
            Assert.NotNull(job);
            Assert.Equal(ProcessingJobStatus.Pending, job.Status);
            Assert.Equal(2, job.RetryCount);
        }

        [Fact]
        public async Task ProcessJob_MarkItemImported()
        {
            var sourceDirectory = FileService.GetTempDirectory("source-directory");
            var file1 = await FileService.GetFileAsync(sourceDirectory, "Target Book.m4b");

            downloadClientGatewayMock.SourceFiles = [file1];

            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithAudiobook(await CreateAudiobook())
                .WithDownloadClientConfiguration(await CreateDownloadClientConfiguration())
                .WithPath(sourceDirectory)
                .WithCompletedStatus(at: DateTime.UtcNow)
                .Build());

            var job = await _downloadProcessingJobRepository.AddAsync(new DownloadProcessingJobBuilder()
                .WithDownload(download)
                .Build());

            Assert.Equal(0, downloadClientGatewayMock.GetCallCount(nameof(downloadClientGatewayMock.MarkItemAsImportedAsync)));

            // Process the job
            var downloadProcessingJobProcessor = _provider.GetRequiredService<DownloadProcessingJobProcessor>();
            await downloadProcessingJobProcessor.ProcessQueueAsync(CancellationToken.None);

            // Job is retried (and refailed)
            job = await _downloadProcessingJobRepository.GetByIdAsync(job.Id);
            Assert.NotNull(job);
            Assert.Equal(ProcessingJobStatus.Completed, job.Status);

            Assert.Equal(1, downloadClientGatewayMock.GetCallCount(nameof(downloadClientGatewayMock.MarkItemAsImportedAsync)));
        }
    }
}
