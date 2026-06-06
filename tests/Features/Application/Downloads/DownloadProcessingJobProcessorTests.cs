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

        // --- Mode 1 duplicate-edition collision discriminator ----------------------------------
        // Empirical context (kevin/live, 2026-06-06): 107/107 ImportBlocked-with-real-data
        // "files reported by client and files on disk do not match" failures involve a same-hash
        // sibling Download. 70% have a sibling already at Status=Moved — the first import moved
        // the torrent's audio into the owning record's library folder; this sibling sees only
        // companion files (covers/PDFs). The brief's "import the audio that's present" fix would
        // import companion jpgs/PDFs as audiobook files — actively wrong. Instead, we discriminate
        // the case at the mismatch branch and fail fast with a clear message naming the real
        // cause (duplicate edition records to deduplicate).

        [Fact]
        [Trait("Method", "ProcessJobAsync")]
        public async Task MismatchedFileCount_WithSameHashSiblingAlreadyMoved_FailsFastWithCollisionMessage()
        {
            // Arrange — a sibling download for a DIFFERENT audiobook record shares this torrent's
            // hash and has already reached Moved: that's the duplicate-edition collision pattern.
            const string sharedHash = "abc123deadbeef0000000000000000000000abcd";

            var movedSibling = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithAudiobook(await CreateAudiobook())
                .WithDownloadClientConfiguration(await CreateDownloadClientConfiguration())
                .WithTorrentHash(sharedHash)
                .WithStatus(DownloadStatus.Moved)
                .Build());

            // The doomed sibling: same hash, different audiobook, no audio on disk (only companions
            // remain — the audio moved with movedSibling's import). We reproduce that by reporting
            // 5 SourceFiles but only creating 1 (a companion) on disk → triggers the mismatch branch.
            var source = FileService.GetTempDirectory("collision-source");
            var companionOnDisk = await FileService.GetFileAsync(source, "cover.jpg");
            downloadClientGatewayMock.SourceFiles = new List<string>
            {
                companionOnDisk,
                Path.Join(source, "chapter01.mp3"),
                Path.Join(source, "chapter02.mp3"),
                Path.Join(source, "chapter03.mp3"),
                Path.Join(source, "chapter04.mp3"),
            };

            var doomed = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithAudiobook(await CreateAudiobook())
                .WithDownloadClientConfiguration(await CreateDownloadClientConfiguration())
                .WithTorrentHash(sharedHash)
                .WithPath(source)
                .WithCompletedStatus(at: DateTime.UtcNow)
                .Build());

            var job = await _downloadProcessingJobRepository.AddAsync(new DownloadProcessingJobBuilder()
                .WithDownload(doomed)
                .Build());

            // Act
            var processor = _provider.GetRequiredService<DownloadProcessingJobProcessor>();
            await processor.ProcessQueueAsync(CancellationToken.None);

            // Assert — job failed immediately (no retry), import was not attempted, the message
            // names the duplicate-edition cause so the eventual dedup migration can find these.
            var refreshed = await _downloadProcessingJobRepository.GetByIdAsync(job.Id);
            Assert.NotNull(refreshed);
            Assert.Equal(ProcessingJobStatus.Failed, refreshed!.Status);
            Assert.Equal(0, refreshed.RetryCount);
            Assert.Contains("Duplicate-edition collision", refreshed.ErrorMessage, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("deduplicat", refreshed.ErrorMessage, StringComparison.OrdinalIgnoreCase);

            downloadImportServiceMock.Verify(m => m.ImportDownloadFilesAsync(
                    It.IsAny<Audiobook>(),
                    It.IsAny<List<string>>(),
                    It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        [Trait("Method", "ProcessJobAsync")]
        public async Task MismatchedFileCount_WithoutMovedSiblingHash_FallsThroughToRetry()
        {
            // Arrange — same mismatch shape as above (5 reported, 1 on disk), but NO sibling at
            // Status=Moved for this hash. This is the 30% case — possibly siblings racing for the
            // same files, or a legitimate transient race. Existing retry behavior must be preserved
            // so we don't preemptively block a download that might still succeed.
            const string uniqueHash = "feeddead0000000000000000000000000000beef";

            var source = FileService.GetTempDirectory("transient-source");
            var oneOnDisk = await FileService.GetFileAsync(source, "audiobook.mp3");
            downloadClientGatewayMock.SourceFiles = new List<string>
            {
                oneOnDisk,
                Path.Join(source, "still-downloading.mp3"),
                Path.Join(source, "also-missing.mp3"),
            };

            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithAudiobook(await CreateAudiobook())
                .WithDownloadClientConfiguration(await CreateDownloadClientConfiguration())
                .WithTorrentHash(uniqueHash)
                .WithPath(source)
                .WithCompletedStatus(at: DateTime.UtcNow)
                .Build());

            var job = await _downloadProcessingJobRepository.AddAsync(new DownloadProcessingJobBuilder()
                .WithDownload(download)
                .Build());

            // Act
            var processor = _provider.GetRequiredService<DownloadProcessingJobProcessor>();
            await processor.ProcessQueueAsync(CancellationToken.None);

            // Assert — scheduled for retry (not failed immediately), preserves prior behavior.
            var refreshed = await _downloadProcessingJobRepository.GetByIdAsync(job.Id);
            Assert.NotNull(refreshed);
            Assert.Equal(ProcessingJobStatus.Pending, refreshed!.Status);
            Assert.Equal(1, refreshed.RetryCount);
            Assert.Contains("Files reported by the download client and files on disk do not match",
                refreshed.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        }
    }
}
