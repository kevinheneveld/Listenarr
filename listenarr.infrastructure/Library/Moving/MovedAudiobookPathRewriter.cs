/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 */
using Listenarr.Domain.Common;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Moving;

internal static class MovedAudiobookPathRewriter
{
    public static async Task RewriteAsync(
        Audiobook audiobook,
        string source,
        string target,
        IAudiobookRepository audiobookRepository,
        ILogger logger)
    {
        await RewriteBasePathAsync(audiobook, target, audiobookRepository, logger);
        await RewriteImagePathAsync(audiobook, source, target, audiobookRepository, logger);
        await RewriteLegacyFilePathAsync(audiobook, source, target, audiobookRepository, logger);
    }

    /// <summary>
    /// The record must follow its files IMMEDIATELY after the physical move.
    /// The processor's post-move scan resolves a null path to BasePath — a
    /// stale BasePath pointed that scan at the just-deleted source folder,
    /// whose missing-folder cleanup then deleted every tracked file row
    /// (live case: 13 records zeroed to 0 files after their moves
    /// "completed" successfully).
    /// </summary>
    private static async Task RewriteBasePathAsync(
        Audiobook audiobook,
        string target,
        IAudiobookRepository audiobookRepository,
        ILogger logger)
    {
        try
        {
            if (string.Equals(audiobook.BasePath, target, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            audiobook.BasePath = target;
            await audiobookRepository.UpdateAsync(audiobook);
            logger.LogInformation(
                "Updated BasePath for audiobook {AudiobookId} to move target", audiobook.Id);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            logger.LogWarning(
                exception,
                "Failed to update BasePath after move for audiobook {AudiobookId}",
                audiobook.Id);
        }
    }

    private static async Task RewriteImagePathAsync(
        Audiobook audiobook,
        string source,
        string target,
        IAudiobookRepository audiobookRepository,
        ILogger logger)
    {
        try
        {
            var imageUrl = audiobook.ImageUrl;
            if (string.IsNullOrWhiteSpace(imageUrl))
            {
                return;
            }

            var looksLikeFileSystemPath = Path.IsPathRooted(imageUrl)
                || imageUrl.StartsWith(source, StringComparison.OrdinalIgnoreCase)
                || imageUrl.StartsWith(
                    source.Replace(Path.DirectorySeparatorChar, '/'),
                    StringComparison.OrdinalIgnoreCase);
            if (!looksLikeFileSystemPath)
            {
                return;
            }

            var fullImagePath = Path.IsPathRooted(imageUrl)
                ? Path.GetFullPath(imageUrl)
                : Path.GetFullPath(Path.Join(source, imageUrl));
            if (!FileUtils.IsPathSameOrInside(fullImagePath, source))
            {
                return;
            }

            var relativePath = Path.GetRelativePath(source, fullImagePath);
            if (FileUtils.TryResolveRelativePathWithinBase(target, relativePath, out var newImagePath)
                && File.Exists(newImagePath))
            {
                audiobook.ImageUrl = newImagePath;
                await audiobookRepository.UpdateAsync(audiobook);
                logger.LogInformation(
                    "Updated ImageUrl for audiobook {AudiobookId} to new path after move",
                    audiobook.Id);
            }
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            logger.LogDebug(
                exception,
                "Non-fatal: failed to update ImageUrl after move for audiobook {AudiobookId}",
                audiobook.Id);
        }
    }

    private static async Task RewriteLegacyFilePathAsync(
        Audiobook audiobook,
        string source,
        string target,
        IAudiobookRepository audiobookRepository,
        ILogger logger)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(audiobook.FilePath))
            {
                return;
            }

            var fullFilePath = Path.IsPathRooted(audiobook.FilePath)
                ? Path.GetFullPath(audiobook.FilePath)
                : Path.GetFullPath(Path.Join(source, audiobook.FilePath));
            if (!FileUtils.IsPathSameOrInside(fullFilePath, source))
            {
                return;
            }

            var relativePath = Path.GetRelativePath(source, fullFilePath);
            if (FileUtils.TryResolveRelativePathWithinBase(target, relativePath, out var newFilePath)
                && File.Exists(newFilePath))
            {
                audiobook.FilePath = newFilePath;
                await audiobookRepository.UpdateAsync(audiobook);
                logger.LogInformation(
                    "Updated FilePath for audiobook {AudiobookId} to new path after move",
                    audiobook.Id);
            }
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException))
        {
            logger.LogDebug(
                exception,
                "Non-fatal: failed to update FilePath after move for audiobook {AudiobookId}",
                audiobook.Id);
        }
    }
}
