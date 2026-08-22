using Listenarr.Application.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library;

public sealed partial class LibraryUpdateWorkflow
{
    private async Task<IActionResult> ApplyMetadataUpdatesAsync(
        int id,
        AudiobookUpdateRequest request,
        bool basePathRewritten,
        bool suppressStaleImageUrl,
        bool metadataUpdateRequested,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var scope = _scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
        var existingAudiobook = await repository.GetByIdAsync(id);
        cancellationToken.ThrowIfCancellationRequested();
        if (existingAudiobook == null)
        {
            return new NotFoundObjectResult(new { message = "Audiobook not found" });
        }

        var legacyIdentifierFieldsTouched = false;
        if (request.Title != null) existingAudiobook.Title = request.Title;
        if (request.Subtitle != null) existingAudiobook.Subtitle = request.Subtitle;
        if (request.Authors != null) existingAudiobook.Authors = request.Authors;
        if (request.ImageUrl != null && !suppressStaleImageUrl)
        {
            existingAudiobook.ImageUrl = request.ImageUrl;
        }
        if (request.PublishYear != null) existingAudiobook.PublishYear = request.PublishYear;
        if (request.PublishedDate != null) existingAudiobook.PublishedDate = request.PublishedDate;
        if (request.Description != null) existingAudiobook.Description = request.Description;
        if (request.Genres != null) existingAudiobook.Genres = request.Genres;
        if (request.Tags != null) existingAudiobook.Tags = request.Tags;
        if (request.Narrators != null) existingAudiobook.Narrators = request.Narrators;
        if (request.Isbn != null)
        {
            existingAudiobook.Isbn = request.Isbn;
            legacyIdentifierFieldsTouched = true;
        }

        if (request.Asin != null)
        {
            existingAudiobook.Asin = request.Asin;
            legacyIdentifierFieldsTouched = true;
        }

        if (request.OpenLibraryId != null)
        {
            existingAudiobook.OpenLibraryId = request.OpenLibraryId;
            legacyIdentifierFieldsTouched = true;
        }

        if (request.Publisher != null) existingAudiobook.Publisher = request.Publisher;
        if (request.Language != null) existingAudiobook.Language = request.Language;
        if (request.Runtime != null) existingAudiobook.Runtime = request.Runtime;
        if (request.Edition != null) existingAudiobook.Edition = request.Edition;
        if (request.Version != null) existingAudiobook.Version = request.Version;

        ApplySeriesMembershipUpdates(existingAudiobook, request);

        if (request.Explicit.HasValue) existingAudiobook.Explicit = request.Explicit.Value;
        if (request.Abridged.HasValue) existingAudiobook.Abridged = request.Abridged.Value;
        if (request.Monitored.HasValue) existingAudiobook.Monitored = request.Monitored.Value;

        if (!basePathRewritten && request.FilePath != null) existingAudiobook.FilePath = request.FilePath;
        if (request.FileSize.HasValue) existingAudiobook.FileSize = request.FileSize;
        if (request.Quality != null) existingAudiobook.Quality = request.Quality;

        await ApplyQualityProfileAsync(
            existingAudiobook,
            request,
            cancellationToken);

        if (legacyIdentifierFieldsTouched)
        {
            AudiobookIdentifierMapper.SyncImportedIdentifiersFromLegacyFields(existingAudiobook);
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (metadataUpdateRequested
                && !await repository.UpdateAsync(existingAudiobook))
            {
                return new NotFoundObjectResult(new
                {
                    message = "Audiobook not found"
                });
            }
        }
        catch (UniqueConstraintViolationException)
        {
            // The unique-ASIN backstop rejected this write — some other row
            // already holds the ASIN we just tried to assign. A bare 409 here
            // gives the caller nothing to act on; look up the conflicting row
            // so the UI can offer a real resolution (merge one record into
            // the other via POST /library/{id}/resolve-asin-conflict) instead
            // of a dead-end error.
            return await BuildAsinConflictResponseAsync(repository, id, existingAudiobook.Asin);
        }

        _logger.LogInformation(
            "Updated audiobook '{Title}' (ID: {Id})",
            LogRedaction.SanitizeText(existingAudiobook.Title),
            id);

        return new OkObjectResult(new
        {
            message = "Audiobook updated successfully",
            audiobook = existingAudiobook
        });
    }

    private static async Task<IActionResult> BuildAsinConflictResponseAsync(
        IAudiobookRepository repository,
        int id,
        string? conflictingAsin)
    {
        var conflict = string.IsNullOrWhiteSpace(conflictingAsin)
            ? null
            : await repository.GetByAsinAsync(conflictingAsin);
        if (conflict == null || conflict.Id == id)
        {
            return new ConflictObjectResult(new
            {
                type = "https://tools.ietf.org/html/rfc9110#section-15.5.10",
                title = "Conflict",
                status = 409,
                code = "asin_conflict",
                detail = "This ASIN is already used by another audiobook in your library."
            });
        }

        // Both sides get a full comparison summary (files, size, bitrate,
        // verification verdicts) — "which record do I keep?" is a quality
        // judgment the user can't make from a title alone.
        var withFiles = await repository.GetByIdsWithFilesAsync(new[] { id, conflict.Id });
        var recordSide = withFiles.FirstOrDefault(a => a.Id == id);
        var conflictSide = withFiles.FirstOrDefault(a => a.Id == conflict.Id);

        return new ConflictObjectResult(new
        {
            type = "https://tools.ietf.org/html/rfc9110#section-15.5.10",
            title = "Conflict",
            status = 409,
            code = "asin_conflict",
            detail = "This ASIN is already used by another audiobook in your library.",
            conflict = SummarizeConflictSide(conflictSide ?? conflict, conflictingAsin),
            record = recordSide == null ? null : SummarizeConflictSide(recordSide, recordSide.Asin)
        });
    }

    private static object SummarizeConflictSide(Audiobook book, string? asin)
    {
        var files = book.Files ?? new List<AudiobookFile>();
        return new
        {
            audiobookId = book.Id,
            title = book.Title,
            authors = book.Authors,
            basePath = book.BasePath,
            asin,
            fileCount = files.Count,
            totalSizeBytes = files.Sum(f => f.Size ?? 0),
            maxBitrate = files.Count > 0 ? files.Max(f => f.Bitrate ?? 0) : 0,
            formats = files
                .Select(f => f.Format)
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Select(f => f!.ToUpperInvariant())
                .Distinct()
                .ToList(),
            runtime = book.Runtime,
            verificationStatus = book.VerificationStatus,
            verificationConfidence = book.VerificationConfidence
        };
    }

}
