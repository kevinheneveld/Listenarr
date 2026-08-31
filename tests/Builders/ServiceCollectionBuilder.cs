using Listenarr.Api.Features.Verification;
using Listenarr.Application.Search.Filters;
using Listenarr.Application.Search.Strategies;
using Listenarr.Infrastructure.DependencyInjection;
using Listenarr.Infrastructure.DependencyInjection.Downloads;
using Listenarr.Infrastructure.Downloads.DirectDownload;
using Listenarr.Infrastructure.HostedServices;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks;
using Listenarr.Tests.Mocks.Api;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Listenarr.Application.Audiobooks.Organizing;

namespace Listenarr.Tests.Builders
{
    /// <summary>
    /// Mock the following by default:
    /// - IDownloadItemService
    /// </summary>
    public class ServiceCollectionBuilder
    {
        private string? _contentRootPath;
        private readonly List<ServiceDescriptor> _serviceDescriptors = new();
        private readonly List<Type> _serviceTypesToRemove = new();
        private Action<DbContextOptionsBuilder>? _dbContextOptionsAction;

        public ServiceCollectionBuilder()
        {
        }

        public ServiceCollectionBuilder WithContentRootPath(string? contentRootPath)
        {
            _contentRootPath = contentRootPath;

            return this;
        }

        public ServiceCollectionBuilder WithMocks(params ServiceDescriptor[] serviceDescriptors)
        {
            return WithMocks((IEnumerable<ServiceDescriptor>)serviceDescriptors);
        }

        public ServiceCollectionBuilder WithMocks(IEnumerable<ServiceDescriptor> serviceDescriptors)
        {
            _serviceDescriptors.AddRange(serviceDescriptors);

            return this;
        }

        public ServiceCollectionBuilder WithSingleton<TService>(TService implementationInstance)
            where TService : class
        {
            _serviceDescriptors.Add(ServiceDescriptor.Singleton(implementationInstance));

            return this;
        }

        public ServiceCollectionBuilder WithSingleton<TService, TImplementation>()
            where TService : class
            where TImplementation : class, TService
        {
            _serviceDescriptors.Add(ServiceDescriptor.Singleton<TService, TImplementation>());

            return this;
        }

        public ServiceCollectionBuilder WithSingleton<TService>(Func<IServiceProvider, TService> implementationFactory)
            where TService : class
        {
            _serviceDescriptors.Add(ServiceDescriptor.Singleton(implementationFactory));

            return this;
        }

        public ServiceCollectionBuilder WithScoped<TService, TImplementation>()
            where TService : class
            where TImplementation : class, TService
        {
            _serviceDescriptors.Add(ServiceDescriptor.Scoped<TService, TImplementation>());

            return this;
        }

        public ServiceCollectionBuilder WithScoped<TService>(Func<IServiceProvider, TService> implementationFactory)
            where TService : class
        {
            _serviceDescriptors.Add(ServiceDescriptor.Scoped(implementationFactory));

            return this;
        }

        public ServiceCollectionBuilder WithTransient<TService, TImplementation>()
            where TService : class
            where TImplementation : class, TService
        {
            _serviceDescriptors.Add(ServiceDescriptor.Transient<TService, TImplementation>());

            return this;
        }

        public ServiceCollectionBuilder WithTransient<TService>(Func<IServiceProvider, TService> implementationFactory)
            where TService : class
        {
            _serviceDescriptors.Add(ServiceDescriptor.Transient(implementationFactory));

            return this;
        }

        /// <summary>
        /// Back the provider's DbContext with a migrated SQLite database on the
        /// given open connection instead of EF InMemory. Use for regression
        /// coverage that depends on real SQLite materialization semantics
        /// (e.g. DateTimeKind round-trips), which InMemory cannot reproduce.
        /// </summary>
        public ServiceCollectionBuilder WithSqliteDatabase(
            Microsoft.Data.Sqlite.SqliteConnection connection)
        {
            _dbContextOptionsAction = options => options.UseSqlite(
                connection,
                sqlite => sqlite.MigrationsAssembly(
                    typeof(Listenarr.Infrastructure.Persistence.ListenArrDbContext)
                        .Assembly.GetName().Name));
            return this;
        }

        public ServiceCollectionBuilder Without<TService>()
        {
            return Without(typeof(TService));
        }

        public ServiceCollectionBuilder Without(params Type[] serviceTypes)
        {
            _serviceTypesToRemove.AddRange(serviceTypes);

            return this;
        }

        public ServiceCollection Build(ServiceCollection? services = null)
        {
            services ??= BuildServices();
            ApplyOverrides(services);

            return services;
        }

        private ServiceCollection BuildServices()
        {
            var configuration = new ConfigurationManager();

            var startupConfigServiceMock = new Mock<IStartupConfigService>();
            startupConfigServiceMock
                .Setup(s => s.GetConfig())
                .Returns(new StartupConfig { AuthenticationRequired = "false" });

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMemoryCache();
            services.TryAddSingleton(TimeProvider.System);
            services.AddSingleton<IWorkerCycleRunner, WorkerCycleRunner>();
            services.AddListenarrAppServices(configuration);
            services.AddListenarrAdapters(configuration);
            services.AddListenarrHttpClients(configuration);
            services.AddListenarrInfrastructure(
                _dbContextOptionsAction
                    ?? (options => options.UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())),
                contentRootPath: _contentRootPath);
            var filesystemReadiness = new LibraryFilesystemReadiness();
            filesystemReadiness.MarkReady();
            services.Replace(ServiceDescriptor.Singleton(filesystemReadiness));
            services.Replace(
                ServiceDescriptor.Scoped<ILibraryAddCommitStore, InMemoryLibraryAddCommitStore>());

            var appMetricsServiceMock = new Mock<IAppMetricsService>();
            services.AddSingleton(appMetricsServiceMock);
            services.AddSingleton(appMetricsServiceMock.Object);

            var webHostEnvironmentMock = new Mock<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
            services.AddSingleton(webHostEnvironmentMock);
            services.AddSingleton(webHostEnvironmentMock.Object);

            var hubClientsMock = new Mock<IHubClients>();
            var clientProxyMock = new Mock<IClientProxy>();

            hubClientsMock.Setup(clients => clients.All).Returns(clientProxyMock.Object);

            var hubContextMock = new Mock<IHubContext<DownloadHub>>();
            hubContextMock.Setup(x => x.Clients).Returns(hubClientsMock.Object);
            services.AddSingleton(hubContextMock.Object);

            services.AddSingleton(startupConfigServiceMock.Object);
            services.AddSingleton(new Mock<IDownloadHistoryService>().Object);
            services.AddSingleton(new Mock<IDiscordBotService>().Object);
            services.AddSingleton<IFfmpegService, FfmpegServiceMock>();
            services.AddSingleton<Listenarr.Application.Audiobooks.Verification.Contracts.IWhisperService, Listenarr.Tests.Mocks.WhisperServiceMock>();
            services.AddSingleton<IAiAssistService, Listenarr.Tests.Mocks.AiAssistServiceMock>();
            services.AddSingleton<Listenarr.Application.Audiobooks.Verification.Contracts.IAudioSampleExtractor, Listenarr.Infrastructure.Ffmpeg.Sampling.AudioSampleExtractor>();
            services.AddSingleton<Listenarr.Application.Audiobooks.Verification.Contracts.IIdentityVerifier, Listenarr.Application.Audiobooks.Verification.DeterministicIdentityVerifier>();
            services.AddSingleton<Listenarr.Application.Audiobooks.Verification.ILibraryVerificationQueueService, Listenarr.Application.Audiobooks.Verification.LibraryVerificationQueueService>();
            services.AddSingleton<IConfigurationService, ConfigurationService>();
            services.AddSingleton<IAudiobookFilesystemDeleteService, AudiobookFilesystemDeleteService>();
            services.AddSingleton<IFileSystemSemanticsResolver, FileSystemSemanticsResolver>();
            services.AddSingleton(new Mock<IRootFolderRelocationService>().Object);
            services.AddSingleton<IMoveQueueService, MoveQueueService>();
            services.AddSingleton<IScanQueueService, ScanQueueService>();
            services.AddSingleton<IRootFolderService, RootFolderService>();
            services.AddSingleton<IAudiobookDestinationRewriteService, AudiobookDestinationRewriteService>();
            services.AddSingleton<MetadataConverters>();
            services.AddSingleton<MetadataMerger>();
            services.AddSingleton<ISearchActivityTracker, SearchActivityTracker>();
            services.AddSingleton<SearchProgressReporter>();
            services.AddSingleton<IndexerAdditionalSettingsParser>();
            services.AddSingleton<IndexerSearchWorkflow>();
            services.AddSingleton<MetadataSourceCatalog>();
            services.AddSingleton<SearchResultFilterPipeline>();
            services.AddSingleton<Listenarr.Application.Search.Contracts.ISearchResultFilter, RelevanceFilter>();
            services.AddSingleton<Listenarr.Application.Search.Contracts.ISearchResultFilter, NumberConflictFilter>();
            services.AddSingleton<MetadataStrategyCoordinator>();
            services.AddSingleton<AsinCandidateCollector>();
            services.AddSingleton<AsinEnricher>();
            services.AddSingleton<SearchResultScorerService>();
            services.AddSingleton<SearchResultSortingService>();
            services.AddSingleton<AsinSearchHandler>();
            services.AddSingleton<DownloadTypeResolver>();
            services.AddSingleton<DownloadClientSelector>();
            services.AddSingleton<DownloadCachedTorrentStore>();
            services.AddSingleton<LibraryMetadataRescanWorkflow>();
            services.AddSingleton<LibraryScanPathResolver>();
            services.AddSingleton<LibraryScanQueueWorkflow>();
            services.AddSingleton<LibraryAddWorkflow>();
            services.AddSingleton<LibraryManualScanWorkflow>();
            services.AddSingleton<LibraryUploadWorkflow>();
            services.AddSingleton<LibraryBulkEditWorkflow>();
            services.AddSingleton<LibraryMoveWorkflow>();
            services.AddSingleton<LibraryDeleteWorkflow>();
            services.AddSingleton<LibraryUpdateWorkflow>();
            services.AddSingleton<LibraryIdentifierWorkflow>();
            services.AddSingleton<LibraryPreviewPathWorkflow>();
            services.AddSingleton<LibraryQueryWorkflow>();
            services.AddSingleton<LibraryRenameWorkflow>();
            services.AddSingleton<DashboardAggregateCache>();
            services.AddSingleton<LibraryTransferFilesWorkflow>();
            services.AddSingleton<LibraryFileDeleteWorkflow>();
            services.AddSingleton<LibrarySplitPreviewWorkflow>();
            services.AddSingleton<LibraryAiSweepWorkflow>();
            services.AddSingleton<LibrarySplitProbeWorkflow>();
            services.AddSingleton<LibrarySeriesEditionsWorkflow>();
            services.AddSingleton<LibraryDashboardStatsWorkflow>();
            services.AddSingleton<LibraryMaintenanceWorkflow>();
            services.AddSingleton<LibrarySeriesBackfillWorkflow>();
            services.AddSingleton<VerificationQueueStatusWorkflow>();
            services.AddSingleton<Listenarr.Infrastructure.Downloads.Cleanup.DownloadHistoryRetentionService>();
            services.AddSingleton<LibraryDuplicatesMergeWorkflow>();
            services.AddSingleton<LibraryMusicCandidatesWorkflow>();
            services.AddSingleton<Listenarr.Application.Audiobooks.Organizing.ILibraryRecoveryFilesystem, Listenarr.Infrastructure.Library.Organizing.LibraryRecoveryFilesystem>();
            services.AddSingleton<LibraryOrganizeSweepWorkflow>();
            services.AddSingleton<LibraryMoveSummaryWorkflow>();
            services.AddSingleton<IOrganizeFilesystem, Listenarr.Infrastructure.Library.Organizing.OrganizeFilesystem>();
            services.AddSingleton<LibraryDuplicatesWorkflow>();
            services.AddSingleton<LibraryDuplicateCopyAnalysisWorkflow>();
            services.AddSingleton<DuplicateCopyProposalBuilder>();
            services.AddSingleton<LibraryDuplicateCopyApplyWorkflow>();
            services.AddSingleton<LibrarySeriesHealthWorkflow>();
            services.AddSingleton<Listenarr.Application.Audiobooks.Verification.IWrongContentAutoRejector, WrongContentAutoRejector>();
            services.AddSingleton<LibraryNotAudiobookWorkflow>();
            services.AddSingleton<LibraryEmbeddedMetadataWorkflow>();
            services.AddSingleton<LibraryFileStreamWorkflow>();
            services.AddSingleton<SearchResponseMapper>();
            services.AddSingleton<ImagePlaceholderResolver>();
            services.AddSingleton<IndexerTestWorkflow>();
            services.AddSingleton<ProwlarrIndexerUpsertWorkflow>();
            services.AddSingleton<ManualImportPathPlanner>();
            services.AddSingleton<ManualImportCompanionImporter>();
            services.AddSingleton<AudibleAuthorPageCollector>();
            services.AddSingleton<AudibleSimpleLookupWorkflow>();
            services.AddSingleton<AudibleAuthorSearchWorkflow>();
            services.AddSingleton<DownloadService>();
            services.AddSingleton<ScanJobProcessor>();
            services.AddSingleton<IScanJobProcessor>(sp => sp.GetRequiredService<ScanJobProcessor>());
            services.AddSingleton<AudiobookContentMoveService>();
            services.AddSingleton<MoveJobProcessor>();
            services.AddSingleton<IMoveJobProcessor>(sp => sp.GetRequiredService<MoveJobProcessor>());
            services.AddSingleton<MoveBackgroundService>();
            services.AddSingleton<MoveQueueService>();
            services.AddSingleton<LibraryController>();
            services.AddSingleton<Listenarr.Api.Features.Verification.VerificationController>();
            services.AddSingleton<ImagesController>();
            services.AddSingleton(new EphemeralDataProtectionProvider().CreateProtector("Listenarr.ConfigurationService.ProwlarrImport"));

            services.AddSingleton<AudibleApiMock>();
            services.AddSingleton<AudnexusServiceApiMock>();
            services.AddSingleton<TransmissionApiMock>();
            services.AddSingleton<SabnzbdApiMock>();
            services.AddSingleton<NzbgetApiMock>();
            services.AddSingleton<QbittorrentApiMock>();
            services.AddSingleton(_ => new MyAnonamouseApiMock
            {
                FailOnUnexpectedCalls = true
            });

            services.AddHttpClient<AudibleService>()
                .ConfigurePrimaryHttpMessageHandler<AudibleApiMock>();

            // FIXME: All classes should rely on typed HttpClient instead of named ones
            // TODO: Find a way to test real http client configurations (cookies, retry, security, ...)
            services.AddHttpClient("transmission")
                .ConfigurePrimaryHttpMessageHandler<TransmissionApiMock>();

            services.AddHttpClient("sabnzbd")
                .ConfigurePrimaryHttpMessageHandler<SabnzbdApiMock>();

            services.AddHttpClient("nzbget")
                .ConfigurePrimaryHttpMessageHandler<NzbgetApiMock>();

            services.AddHttpClient("qbittorrent")
                .ConfigurePrimaryHttpMessageHandler<QbittorrentApiMock>();

            services.AddHttpClient("DirectDownload");

            services.AddHttpClient(DownloadRegistrationExtensions.MyAnonamouseTorrentClientName)
                .ConfigurePrimaryHttpMessageHandler<MyAnonamouseApiMock>();

            services.AddHttpClient<IAudnexusService, AudnexusService>()
                .ConfigurePrimaryHttpMessageHandler<AudnexusServiceApiMock>();

            services.AddSingleton<IDownloadClientAdapter, DownloadCLientAdapterMock>();
            services.AddSingleton<IDirectDownloadImportSourceResolver, DirectDownloadImportSourceResolver>();

            // Background services
            services.AddSingleton<DownloadMonitorProcessor>();
            services.AddSingleton<IDownloadMonitorProcessor>(sp => sp.GetRequiredService<DownloadMonitorProcessor>());
            services.AddSingleton<DownloadMonitorService>();
            services.AddSingleton<DirectDownloadProcessor>();
            services.AddSingleton<IDirectDownloadProcessor>(sp => sp.GetRequiredService<DirectDownloadProcessor>());
            services.AddSingleton<DirectDownloadService>();
            services.AddSingleton<DownloadProcessingJobProcessor>();
            services.AddSingleton<IDownloadImportProcessor>(sp => sp.GetRequiredService<DownloadProcessingJobProcessor>());
            services.AddSingleton<DownloadProcessingJobCleanupProcessor>();
            services.AddSingleton<IDownloadProcessingJobCleanupProcessor>(sp => sp.GetRequiredService<DownloadProcessingJobCleanupProcessor>());
            services.AddSingleton<DownloadProcessingJobCleanupService>();

            return services;
        }

        private void ApplyOverrides(ServiceCollection services)
        {
            foreach (var serviceDescriptor in _serviceDescriptors)
            {
                services.RemoveAll(serviceDescriptor.ServiceType);
            }

            foreach (var serviceType in _serviceTypesToRemove)
            {
                services.RemoveAll(serviceType);
            }

            foreach (var serviceDescriptor in _serviceDescriptors)
            {
                services.Add(serviceDescriptor);
            }
        }
    }
}
