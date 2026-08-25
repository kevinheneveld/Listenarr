/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Application.Audiobooks.Deletion;
using Listenarr.Application.Audiobooks.RootFolders;
using Listenarr.Infrastructure.Library.Realtime;
using Listenarr.Infrastructure.Persistence;
using Listenarr.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Listenarr.Application.Audiobooks.Organizing;

using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Listenarr.Infrastructure.DependencyInjection.Library;

internal static class LibraryRegistrationExtensions
{
    public static IServiceCollection AddLibraryServices(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<IFilesystemMutationCoordinator, FilesystemMutationCoordinator>();
        services.AddSingleton<IDirectoryObjectIdentityResolver, DirectoryObjectIdentityResolver>();
        services.AddSingleton<IRootFolderStorageHealthResolver, RootFolderStorageHealthResolver>();
        services.AddSingleton<LibraryDirectoryOwnershipBoundaryAuthorizer>();
        services.AddSingleton<IAudiobookOperationCoordinator, AudiobookOperationCoordinator>();
        services.AddSingleton<IAudiobookUpdatePublisher, AudiobookUpdatePublisher>();
        services.AddSingleton<IRootFolderRelocationService, RootFolderRelocationService>();
        services.AddSingleton<IMoveCleanupBoundaryResolver, MoveCleanupBoundaryResolver>();
        services.AddSingleton<ILibraryDirectoryOwnershipStore, EfLibraryDirectoryOwnershipStore>();
        services.AddSingleton<IAudiobookDeletionIntentProbe, AudiobookDeletionIntentProbe>();
        services.AddSingleton<IFileRegistrationRecoveryProbe, FileRegistrationRecoveryProbe>();
        services.AddSingleton<IFileRenameRecoveryProbe, FileRenameRecoveryProbe>();
        services.AddSingleton<IMoveQueueService, MoveQueueService>();
        services.AddScoped<IAudiobookDeletionCommitService, AudiobookDeletionCommitService>();
        services.AddScoped<IAudiobookDeletionIntentStore, AudiobookDeletionIntentStore>();
        services.AddScoped<IAudiobookDeletionIntentReconciler, AudiobookDeletionIntentReconciler>();
        services.AddScoped<IRootFolderStorageConfirmationService, RootFolderStorageConfirmationService>();
        services.AddScoped<IAudiobookFilePathIdentityResolver, AudiobookFilePathIdentityResolver>();
        services.AddScoped<IFileRenameCommitStore, FileRenameCommitStore>();
        services.AddScoped<IFileRegistrationRecoveryService, FileRegistrationRecoveryService>();
        services.AddScoped<CompatibilityFilePublicationRecoveryService>();
        services.AddScoped<ICompatibilityFilePublicationRecoveryService>(provider =>
            provider.GetRequiredService<CompatibilityFilePublicationRecoveryService>());
        services.AddScoped<IFileRenameRecoveryReconciler, FileRenameRecoveryReconciler>();
        services.AddScoped<IAudiobookFileIdentityReconciler, AudiobookFileIdentityReconciler>();
        services.AddScoped<IRootFolderObjectIdentityReconciler, RootFolderObjectIdentityReconciler>();
        services.AddScoped<ILibraryDirectoryOwnershipReconciler, LibraryDirectoryOwnershipReconciler>();
        services.AddScoped<IAudiobookFileService, AudiobookFileService>();
        // Singleton: stateless filesystem primitives, consumed by the singleton MoveJobProcessor.
        services.AddSingleton<IOrganizeFilesystem, Listenarr.Infrastructure.Library.Organizing.OrganizeFilesystem>();
        // Singleton: stateless disk probes for the recovery/maintenance flows.
        services.AddSingleton<ILibraryRecoveryFilesystem, Listenarr.Infrastructure.Library.Organizing.LibraryRecoveryFilesystem>();

        // Audio identity verification (ADR-0001). Whisper is scoped (not
        // singleton): it reads scoped IConfigurationService per transcription.
        // Long timeout: the escalation model download is ~466 MB on first use.
        services.AddHttpClient("WhisperModels", client => client.Timeout = TimeSpan.FromMinutes(30));
        services.AddScoped<Listenarr.Application.Audiobooks.Verification.Contracts.IWhisperService, Listenarr.Infrastructure.Whisper.WhisperService>();
        services.AddScoped<Listenarr.Application.Audiobooks.Verification.Contracts.IAudioSampleExtractor, Listenarr.Infrastructure.Ffmpeg.Sampling.AudioSampleExtractor>();
        services.AddScoped<Listenarr.Application.Audiobooks.Verification.Contracts.IIdentityVerifier, Listenarr.Application.Audiobooks.Verification.DeterministicIdentityVerifier>();
        // The queue is a singleton: it owns the in-memory job map + channel.
        services.AddSingleton<Listenarr.Application.Audiobooks.Verification.ILibraryVerificationQueueService, Listenarr.Application.Audiobooks.Verification.LibraryVerificationQueueService>();

        services.AddScoped<IScanPathAuthorizationService, ScanPathAuthorizationService>();
        services.AddScoped<IAudiobookScanService, AudiobookScanService>();
        services.AddScoped<MoveSourceManifestService>();
        services.AddScoped<IMoveSourceManifestService>(serviceProvider =>
            serviceProvider.GetRequiredService<MoveSourceManifestService>());
        services.AddScoped<IMoveSourcePlanService>(serviceProvider =>
            serviceProvider.GetRequiredService<MoveSourceManifestService>());
        services.AddScoped<IAuthorCatalogService, AuthorCatalogService>();
        services.AddScoped<ISeriesCatalogService, SeriesCatalogService>();
        services.AddScoped<ILibraryDestinationMutationGuard, LibraryDestinationMutationGuard>();
        services.AddScoped<ILibraryAddService, LibraryAddService>();
        services.AddScoped<Listenarr.Application.Audiobooks.Contracts.IFileExtractionService, Listenarr.Application.Audiobooks.Files.FileExtractionService>();
        services.AddScoped<IAudiobookFilesystemDeleteService, AudiobookFilesystemDeleteService>();
        // Admin-triggered backlog sweep: localizes external cover-art URLs.
        services.AddScoped<Listenarr.Application.Common.Images.IExternalCoverArtSweepService, Listenarr.Application.Common.Images.ExternalCoverArtSweepService>();
        services.AddScoped<ILibraryListService, LibraryListService>();
        services.AddScoped<IAuthorMonitoringService, AuthorMonitoringService>();
        services.AddScoped<ISeriesMonitoringService, SeriesMonitoringService>();
        services.AddScoped<IFileNamingService, FileNamingService>();
        services.AddScoped<IRenameService, RenameService>();
        services.AddScoped<IQualityProfileService, QualityProfileService>();
        return services;
    }

    public static IServiceCollection AddLibraryInfrastructure(this IServiceCollection services)
    {
        services.AddScoped<IAudiobookRepository, AudiobookRepository>();
        services.AddScoped<ILibraryAddCommitStore, EfLibraryAddCommitStore>();
        services.AddScoped<IQualityProfileRepository, QualityProfileRepository>();
        services.AddScoped<IAudiobookFileRepository, EfAudiobookFileRepository>();
        services.AddScoped<IBlockedReleaseRepository, EfBlockedReleaseRepository>();
        services.AddScoped<IVerificationJobRepository, EfVerificationJobRepository>();
        services.AddScoped<IMoveJobRepository, EfMoveJobRepository>();
        services.AddScoped<IMonitoredAuthorRepository, EfMonitoredAuthorRepository>();
        services.AddScoped<IMonitoredSeriesRepository, EfMonitoredSeriesRepository>();
        services.AddScoped<IAuthorMonitoringExclusionRepository, EfAuthorMonitoringExclusionRepository>();
        services.AddScoped<IRootFolderRepository, EfRootFolderRepository>();
        return services;
    }
}
