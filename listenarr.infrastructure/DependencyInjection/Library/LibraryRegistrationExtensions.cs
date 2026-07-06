/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */
using Listenarr.Infrastructure.Persistence.Repositories;
using Microsoft.Extensions.DependencyInjection;
using Listenarr.Application.Audiobooks.Organizing;

namespace Listenarr.Infrastructure.DependencyInjection.Library;

internal static class LibraryRegistrationExtensions
{
    public static IServiceCollection AddLibraryServices(this IServiceCollection services)
    {
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
        services.AddScoped<IAuthorCatalogService, AuthorCatalogService>();
        services.AddScoped<ISeriesCatalogService, SeriesCatalogService>();
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
