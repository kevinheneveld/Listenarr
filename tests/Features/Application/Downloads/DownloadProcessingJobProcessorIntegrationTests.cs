using Listenarr.Application.Audiobooks.Verification;
using Listenarr.Application.Downloads;
using Listenarr.Application.Interfaces;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Listenarr.Tests.Features.Application.Downloads
{
    [Trait("Name", "DownloadProcessingJobProcessorIntegrationTests")]
    [Trait("Category", "DownloadProcessingJob")]
    public class DownloadProcessingJobProcessorIntegrationTests : BaseTests
    {
        private DownloadProcessingJobProcessor downloadProcessingJobProcessor = null!;
        private IDownloadProcessingJobService downloadProcessingJobService = null!;
        private DownloadClientGatewayMock downloadClientGateway = new();

        public override async Task InitializeAsync()
        {
            _services.AddSingleton<IDownloadClientGateway>(downloadClientGateway);
            Init();
            await InitData();
        }

        private async Task InitData()
        {
            downloadProcessingJobProcessor = _provider.GetRequiredService<DownloadProcessingJobProcessor>();
            downloadProcessingJobService = _provider.GetRequiredService<IDownloadProcessingJobService>();
        }

        [Fact]
        public async Task ProcessJob_HappyPath()
        {
            var sourceDir = FileService.GetTempDirectory("dl-dir");
            var destRoot = FileService.GetTempDirectory("dl-dest");

            var audioPath = await FileService.GetFileAsync(sourceDir, "book.m4b");
            var coverPath = await FileService.GetFileAsync(sourceDir, "cover.jpg");
            var txtPath = await FileService.GetFileAsync(sourceDir, "book.txt");

            downloadClientGateway.SourceFiles = [audioPath, coverPath, txtPath];

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithBasePath(destRoot)
                .Build());

            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithCompletedStatus(DateTime.UtcNow)
                .WithPath(sourceDir)
                .WithStartDate(DateTime.UtcNow)
                .WithDownloadClientConfiguration(await CreateDownloadClientConfiguration())
                .WithAudiobook(audiobook)
                .Build());

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithMoveFileOnCompleted()
                .WithoutMetadataProcessing()
                .Build());

            var expectedAudioDest = Path.Join(destRoot, "book.m4b");
            var expectedCoverDest = Path.Join(destRoot, "cover.jpg");
            var expectedTxtDest = Path.Join(destRoot, "book.txt");

            var jobId = await downloadProcessingJobService.EnqueueAsync(download);
            Assert.NotEmpty(jobId);
            var job = await _downloadProcessingJobRepository.GetByIdAsync(jobId);
            Assert.NotNull(job);

            Assert.False(File.Exists(expectedAudioDest));
            Assert.False(File.Exists(expectedCoverDest));
            Assert.False(File.Exists(expectedTxtDest));
            Assert.Equal(sourceDir, download.DownloadPath);

            await downloadProcessingJobProcessor.ProcessQueueAsync(CancellationToken.None);

            job = await _downloadProcessingJobRepository.GetByIdAsync(job.Id);

            Assert.True(File.Exists(expectedAudioDest));
            Assert.True(File.Exists(expectedCoverDest));
            Assert.True(File.Exists(expectedTxtDest));
            Assert.Equal(sourceDir, download.DownloadPath);
        }

        [Fact]
        public async Task ProcessQueueAsync_DirectorySource_UsesClientReportedFilesToExcludeUnrelatedFiles()
        {
            var sourceDir = FileService.GetTempDirectory($"dl-dir");
            var destRoot = FileService.GetTempDirectory($"dl-dest");

            var audioPath = await FileService.GetFileAsync(sourceDir, "book.m4b");
            var coverPath = await FileService.GetFileAsync(sourceDir, "cover.jpg");
            var txtPath = await FileService.GetFileAsync(sourceDir, "book.txt");
            var unrelatedPath = await FileService.GetFileAsync(sourceDir, "unrelated.txt");

            var downloadClientGatewayMock = new DownloadClientGatewayMock
            {
                SourceFiles = [audioPath, coverPath, txtPath]
            };
            _services.AddSingleton<IDownloadClientGateway>(downloadClientGatewayMock);
            _services.Replace(new ServiceDescriptor(typeof(IDownloadItemService), typeof(DownloadItemService), ServiceLifetime.Singleton));
            Init();
            await InitData();

            await _applicationSettingsRepository.SaveAsync(new ApplicationSettingsBuilder()
                .WithOutputPath(destRoot)
                .WithCopyFileOnCompleted()
                .WithoutMetadataProcessing()
                .Build());

            var client = await _downloadClientConfigurationRepository.SaveAsync(new DownloadClientConfigurationBuilder()
                .WithId("client-1")
                .Build());

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithBasePath(destRoot)
                .Build());

            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithCompletedStatus(DateTime.UtcNow)
                .WithPath(sourceDir)
                .WithStartDate(DateTime.UtcNow)
                .WithDownloadClientConfiguration(client)
                .WithAudiobook(audiobook)
                .Build());

            var jobId = await downloadProcessingJobService.EnqueueAsync(download);
            Assert.NotEmpty(jobId);
            var job = await _downloadProcessingJobRepository.GetByIdAsync(jobId);
            Assert.NotNull(job);

            await downloadProcessingJobProcessor.ProcessQueueAsync(CancellationToken.None);

            Assert.True(File.Exists(Path.Join(destRoot, "book.m4b")));
            Assert.True(File.Exists(Path.Join(destRoot, "cover.jpg")));
            Assert.True(File.Exists(Path.Join(destRoot, "book.txt")));
            Assert.False(File.Exists(Path.Join(destRoot, "unrelated.txt")));
        }

        [Fact]
        public async Task ProcessJob_EnqueuesVerifyOnImport_WhenEnabledAndWhisperAvailable()
        {
            _services.AddScoped<IWhisperService>(_ => new AvailableWhisperServiceStub());
            Init();
            await InitData();

            var (audiobook, _) = await SetUpSuccessfulImportAsync();

            await downloadProcessingJobProcessor.ProcessQueueAsync(CancellationToken.None);

            var queue = _provider.GetRequiredService<ILibraryVerificationQueueService>();
            Assert.True(queue.Reader.TryRead(out var verificationJob));
            Assert.NotNull(verificationJob);
            Assert.Equal(new List<int> { audiobook.Id }, verificationJob!.AudiobookIds);
            Assert.Equal(VerificationTriggers.Import, verificationJob.Trigger);
            Assert.False(queue.Reader.TryRead(out _)); // exactly one job per import
        }

        [Fact]
        public async Task ProcessJob_DoesNotEnqueueVerifyOnImport_WhenSettingDisabled()
        {
            _services.AddScoped<IWhisperService>(_ => new AvailableWhisperServiceStub());
            Init();
            await InitData();

            await SetUpSuccessfulImportAsync(settings => settings.VerificationOnImport = false);

            await downloadProcessingJobProcessor.ProcessQueueAsync(CancellationToken.None);

            var queue = _provider.GetRequiredService<ILibraryVerificationQueueService>();
            Assert.False(queue.Reader.TryRead(out _));
        }

        [Fact]
        public async Task ProcessJob_DoesNotEnqueueVerifyOnImport_WhenWhisperUnavailable()
        {
            // No whisper stub: the real WhisperService finds no binary in the test
            // environment, so the import must skip the enqueue rather than queue a
            // job that can only fail.
            var (_, jobId) = await SetUpSuccessfulImportAsync();

            await downloadProcessingJobProcessor.ProcessQueueAsync(CancellationToken.None);

            // The import itself must still complete.
            var job = await _downloadProcessingJobRepository.GetByIdAsync(jobId);
            Assert.NotNull(job);

            var queue = _provider.GetRequiredService<ILibraryVerificationQueueService>();
            Assert.False(queue.Reader.TryRead(out _));
        }

        /// <summary>
        /// Arranges a download that will import successfully (mirrors ProcessJob_HappyPath),
        /// returning the audiobook and the processing job id.
        /// </summary>
        private async Task<(Listenarr.Domain.Models.Audiobook audiobook, string jobId)> SetUpSuccessfulImportAsync(
            Action<Listenarr.Domain.Models.Configurations.ApplicationSettings>? mutateSettings = null)
        {
            var sourceDir = FileService.GetTempDirectory("dl-dir");
            var destRoot = FileService.GetTempDirectory("dl-dest");

            var audioPath = await FileService.GetFileAsync(sourceDir, "book.m4b");
            downloadClientGateway.SourceFiles = [audioPath];

            var audiobook = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithBasePath(destRoot)
                .Build());

            var download = await _downloadRepository.AddAsync(new DownloadBuilder()
                .WithCompletedStatus(DateTime.UtcNow)
                .WithPath(sourceDir)
                .WithStartDate(DateTime.UtcNow)
                .WithDownloadClientConfiguration(await CreateDownloadClientConfiguration())
                .WithAudiobook(audiobook)
                .Build());

            var settings = new ApplicationSettingsBuilder()
                .WithMoveFileOnCompleted()
                .WithoutMetadataProcessing()
                .Build();
            mutateSettings?.Invoke(settings);
            await _applicationSettingsRepository.SaveAsync(settings);

            var jobId = await downloadProcessingJobService.EnqueueAsync(download);
            Assert.NotEmpty(jobId);
            return (audiobook, jobId);
        }

        private sealed class AvailableWhisperServiceStub : IWhisperService
        {
            public Task<bool> IsAvailableAsync() => Task.FromResult(true);
            public string ModelName => "stub";
            public Task<string?> TranscribeAsync(string wavPath, CancellationToken cancellationToken = default)
                => Task.FromResult<string?>(null);
        }
    }
}
