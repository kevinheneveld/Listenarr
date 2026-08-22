/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Domain.Common;
using Listenarr.Infrastructure.FileSystem;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Moving;

internal sealed partial class AudiobookContentMoveService
{
    /// <summary>
    /// Delete a target directory that holds only stale non-audio leftovers
    /// (a metadata stub) so the move can proceed into a fresh target. Runs
    /// only when the job was queued with the replace-stub flag; every claim
    /// the enqueue-time preview made is re-verified here at execute time:
    /// the target must be unowned in the durable ownership store, hold no
    /// audio anywhere in its tree, and be referenced by no database row.
    /// Any doubt falls through to the normal occupied-target refusal.
    /// </summary>
    private async Task TryReplaceStubTargetAsync(
        AudiobookContentMoveRequest request,
        string source,
        string target,
        CancellationToken cancellationToken)
    {
        if (!request.ReplaceStubTarget || !Directory.Exists(target))
        {
            return;
        }

        // A durably owned target is managed content, never a replaceable stub.
        var ownershipResolution = await directoryOwnershipStore.ResolveOwnedAsync(
            target,
            request.TargetSemantics,
            cancellationToken);
        if (ownershipResolution.State != LibraryDirectoryOwnershipResolutionState.Unowned)
        {
            return;
        }

        if (!FileSystemSafety.TryEnumerateTreeWithoutLinks(
                target,
                out var files,
                out var directories,
                out var enumerateReason))
        {
            throw new MoveNeedsAttentionException(
                $"The occupied target could not be inspected for stub replacement: {enumerateReason}");
        }

        if (files.Any(FileUtils.IsAudioFile))
        {
            return;
        }

        await using (var db = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            var basePaths = await db.Audiobooks
                .AsNoTracking()
                .Select(audiobook => audiobook.BasePath)
                .ToListAsync(cancellationToken);
            var trackedPaths = await db.AudiobookFiles
                .AsNoTracking()
                .Select(file => file.Path)
                .ToListAsync(cancellationToken);
            var referenced = basePaths
                .Concat(trackedPaths)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Any(path => IsSameOrInside(path!, target, request.TargetSemantics));
            if (referenced)
            {
                return;
            }
        }

        try
        {
            foreach (var file in files)
            {
                File.Delete(file);
            }

            foreach (var directory in directories
                .OrderByDescending(candidate => candidate.Length))
            {
                Directory.Delete(directory, recursive: false);
            }

            Directory.Delete(target, recursive: false);
        }
        catch (Exception exception) when (exception is
            ArgumentException or IOException or UnauthorizedAccessException
                or InvalidOperationException or NotSupportedException
                or PathTooLongException or System.ComponentModel.Win32Exception)
        {
            throw new MoveNeedsAttentionException(
                $"The stub target could not be deleted for replacement: {exception.Message}");
        }

        logger.LogInformation(
            "Move job {JobId} replaced stub target {Target}: deleted {FileCount} non-audio leftover file(s) and {DirectoryCount} subdirectorie(s)",
            request.JobId,
            LogRedaction.SanitizeFilePath(target),
            files.Count,
            directories.Count);
    }
}
