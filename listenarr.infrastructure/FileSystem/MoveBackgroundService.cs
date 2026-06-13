/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 *
 * This program is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU Affero General Public License for more details.
 *
 * You should have received a copy of the GNU Affero General Public License
 * along with this program. If not, see <https://www.gnu.org/licenses/>.
 */
using Listenarr.Application.Interfaces;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Application.Mapping;
using Listenarr.Application.Notification;
using Listenarr.Application.Security;
using Listenarr.Domain.Common;
using Listenarr.Domain.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.FileSystem
{
    public class MoveBackgroundService(
        IMoveQueueService moveQueueService,
        IToastService toastService,
        IScanQueueService scanQueueService,
        ILogger<MoveBackgroundService> logger,
        IServiceScopeFactory scopeFactory,
        IHubContext<DownloadHub> hubContext) : BackgroundService
    {
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Rehydrate any jobs persisted as Queued/Processing in the DB but
            // not yet in the in-memory channel — happens after a process
            // restart, where the channel state was lost but the DB rows
            // remain. Stale-Processing threshold of 5 minutes is longer than
            // the slowest realistic single-file copy on a home NAS and short
            // enough that a true orphan doesn't sit forever.
            try
            {
                var rehydrated = await moveQueueService.RehydratePendingAsync(TimeSpan.FromMinutes(5), stoppingToken);
                if (rehydrated > 0)
                {
                    logger.LogInformation("MoveBackgroundService: rehydrated {Count} pending move job(s) at startup", rehydrated);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(ex, "MoveBackgroundService: rehydration at startup failed; continuing with empty channel");
            }

            try
            {
                await foreach (var job in moveQueueService.Reader.ReadAllAsync(stoppingToken))
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    try
                    {
                        // Defensive DB recheck — between the time this job
                        // was enqueued and now, the operator may have hit
                        // POST /library/move/cancel-stale (or some other
                        // path flipped the DB status). The channel doesn't
                        // know about that mutation, so re-check the row's
                        // current state. Skip cancelled / completed jobs.
                        using (var preScope = scopeFactory.CreateScope())
                        {
                            var preRepo = preScope.ServiceProvider.GetRequiredService<IMoveJobRepository>();
                            var freshJob = await preRepo.GetByIdAsync(job.Id, stoppingToken);
                            if (freshJob != null && !string.Equals(freshJob.Status, "Queued", StringComparison.OrdinalIgnoreCase)
                                && !string.Equals(freshJob.Status, "Processing", StringComparison.OrdinalIgnoreCase))
                            {
                                logger.LogInformation("Skipping move job {JobId} — DB status is {Status} (cancelled or already terminal)", job.Id, freshJob.Status);
                                continue;
                            }
                        }

                        logger.LogInformation("Processing move job {JobId} for audiobook {AudiobookId} to {Path}", job.Id, job.AudiobookId, LogRedaction.SanitizeFilePath(job.RequestedPath));
                        moveQueueService.UpdateJobStatus(job.Id, "Processing");

                        using var scope = scopeFactory.CreateScope();
                        var audiobookRepository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
                        var moveJobRepository = scope.ServiceProvider.GetRequiredService<IMoveJobRepository>();

                        var audiobook = await audiobookRepository.GetByIdAsync(job.AudiobookId);
                        if (audiobook == null)
                        {
                            moveQueueService.UpdateJobStatus(job.Id, "Failed", "Audiobook not found");
                            continue;
                        }

                        // Prefer an enqueued source path snapshot if provided, otherwise use the audiobook's BasePath
                        var source = job.SourcePath;
                        if (!string.IsNullOrWhiteSpace(source) && !Directory.Exists(source))
                        {
                            logger.LogWarning("Provided source path {Source} for job {JobId} does not exist; falling back to audiobook.BasePath", LogRedaction.SanitizeFilePath(source), job.Id);
                            source = null;
                        }

                        if (string.IsNullOrWhiteSpace(source))
                        {
                            source = audiobook.BasePath;
                        }

                        if (string.IsNullOrWhiteSpace(source) || !Directory.Exists(source))
                        {
                            moveQueueService.UpdateJobStatus(job.Id, "Failed", "Source path invalid or does not exist");
                            continue;
                        }

                        var requested = job.RequestedPath ?? string.Empty;

                        string target = requested;

                        if (string.IsNullOrWhiteSpace(target))
                        {
                            moveQueueService.UpdateJobStatus(job.Id, "Failed", "Target path not provided");
                            continue;
                        }

                        // Normalize full paths
                        target = Path.GetFullPath(target);
                        source = Path.GetFullPath(source);

                        // If source == target, nothing to do
                        if (string.Equals(source.TrimEnd(Path.DirectorySeparatorChar), target.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                        {
                            moveQueueService.UpdateJobStatus(job.Id, "Completed");
                            continue;
                        }

                        // Delegate the actual copy-and-finalize to MoveExecutor.
                        // It picks a safe staging-directory location (critical
                        // when the configured folder pattern makes target a
                        // descendant of source — e.g. adding {Narrator} — or
                        // we'd recursively copy our own output into oblivion),
                        // and caps error messages so a runaway path-length
                        // failure doesn't bloat History.
                        // Track the executor's chosen temp path for the failure
                        // cleanup branch below.
                        string? attemptedTempPath = null;
                        try
                        {
                            var outcome = await MoveExecutor.ExecuteMoveAsync(source, target, job.Id, logger, stoppingToken, job.ReplaceStubTarget);
                            attemptedTempPath = outcome.TempPathUsed;

                            if (!outcome.Success)
                            {
                                // Surface as a normal failure so the rest of the
                                // failure path (history, attempt counter, toast)
                                // runs uniformly.
                                throw new Exception(outcome.ErrorMessage ?? "Move failed for unknown reason");
                            }

                            logger.LogInformation("Move job {JobId}: copied {Count} files via {Temp}", job.Id, outcome.FilesCopied, LogRedaction.SanitizeFilePath(outcome.TempPathUsed ?? string.Empty));

                            // Persist the new BasePath BEFORE enqueueing the post-move scan.
                            // Without this, the post-move scan (line ~370) runs against the
                            // audiobook's stale BasePath (still pointing at the now-empty
                            // source), reports every tracked file as "missing", clears the
                            // AudiobookFile rows via the "base path missing" cleanup, and
                            // nulls BasePath — orphaning the just-moved files on disk from
                            // the library record. Update BasePath in place so the scan sees
                            // the new location and re-attaches the (now-moved) files. The
                            // ImageUrl / FilePath updates below depend on this being
                            // persisted too, so do it first.
                            try
                            {
                                audiobook.BasePath = FileUtils.NormalizeStoredPath(target);
                                await audiobookRepository.UpdateAsync(audiobook);
                                logger.LogInformation(
                                    "Updated audiobook {AudiobookId} BasePath to new target after move job {JobId}",
                                    audiobook.Id, job.Id);
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                            {
                                // Don't fail the move if the DB update hiccups — the files
                                // are already at the target on disk. A subsequent re-scan
                                // or organize pass can still recover. But log loudly so the
                                // operator sees it.
                                logger.LogError(ex,
                                    "Failed to persist new BasePath after move job {JobId} (audiobook {AudiobookId}). Files are at {Target} but the DB still points at the old source. Re-scan needed.",
                                    job.Id, audiobook.Id, LogRedaction.SanitizeFilePath(target));
                            }

                            // Preserve local image path if it pointed inside the source directory
                            try
                            {
                                if (!string.IsNullOrWhiteSpace(audiobook.ImageUrl))
                                {
                                    var imageUrl = audiobook.ImageUrl;

                                    // Only attempt to rewrite file-system paths (skip /api/v1/images/ or URLs)
                                    bool looksLikeFsPath = Path.IsPathRooted(imageUrl) || imageUrl.StartsWith(source, StringComparison.OrdinalIgnoreCase) || imageUrl.StartsWith(source.Replace(Path.DirectorySeparatorChar, '/'), StringComparison.OrdinalIgnoreCase);
                                    if (looksLikeFsPath)
                                    {
                                        try
                                        {
                                            var fullImagePath = Path.IsPathRooted(imageUrl)
                                                ? Path.GetFullPath(imageUrl)
                                                : Path.GetFullPath(CombineWithOptionalBase(source, imageUrl));
                                            if (fullImagePath.StartsWith(source, StringComparison.OrdinalIgnoreCase))
                                            {
                                                var rel = Path.GetRelativePath(source, fullImagePath);
                                                var newImagePath = Path.GetFullPath(CombineWithOptionalBase(target, rel));

                                                // Only update if the new file actually exists after move
                                                if (System.IO.File.Exists(newImagePath))
                                                {
                                                    audiobook.ImageUrl = newImagePath;
                                                    await audiobookRepository.UpdateAsync(audiobook);
                                                    logger.LogInformation("Updated ImageUrl for audiobook {AudiobookId} to new path after move", audiobook.Id);
                                                }
                                            }
                                        }
                                        catch (Exception innerEx) when (innerEx is not OperationCanceledException && innerEx is not OutOfMemoryException && innerEx is not StackOverflowException)
                                        {
                                            logger.LogDebug(innerEx, "Non-fatal: failed to update ImageUrl after move for audiobook {AudiobookId}", audiobook.Id);
                                        }
                                    }
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                            {
                                logger.LogDebug(ex, "Non-fatal: error while attempting to preserve ImageUrl for audiobook {AudiobookId}", audiobook.Id);
                            }

                            // Preserve legacy single-file FilePath if it pointed inside the source directory
                            try
                            {
                                if (!string.IsNullOrWhiteSpace(audiobook.FilePath))
                                {
                                    var fullFilePath = Path.IsPathRooted(audiobook.FilePath)
                                        ? Path.GetFullPath(audiobook.FilePath)
                                        : Path.GetFullPath(CombineWithOptionalBase(source, audiobook.FilePath));

                                    if (fullFilePath.StartsWith(source, StringComparison.OrdinalIgnoreCase))
                                    {
                                        var rel = Path.GetRelativePath(source, fullFilePath);
                                        var newFilePath = Path.GetFullPath(CombineWithOptionalBase(target, rel));

                                        // Only update if the new file actually exists after move
                                        if (File.Exists(newFilePath))
                                        {
                                            audiobook.FilePath = newFilePath;
                                            await audiobookRepository.UpdateAsync(audiobook);
                                            logger.LogInformation("Updated FilePath for audiobook {AudiobookId} to new path after move", audiobook.Id);
                                        }
                                    }
                                }
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                            {
                                logger.LogDebug(ex, "Non-fatal: failed to update FilePath after move for audiobook {AudiobookId}", audiobook.Id);
                            }

                            // Add history entry and send notifications for the move
                            try
                            {
                                var historyEntry = new History
                                {
                                    AudiobookId = audiobook.Id,
                                    AudiobookTitle = audiobook.Title,
                                    EventType = "Moved",
                                    Message = $"Moved audiobook files from {source} to {target}",
                                    Source = "Move",
                                    Timestamp = DateTime.UtcNow,
                                    NotificationSent = false,
                                    Data = System.Text.Json.JsonSerializer.Serialize(new
                                    {
                                        JobId = job.Id,
                                        Source = source,
                                        Target = target
                                    })
                                };

                                var historyRepository = scope.ServiceProvider.GetRequiredService<IHistoryRepository>();
                                await historyRepository.AddAsync(historyEntry);
                                logger.LogInformation("Added history entry for move job {JobId}", job.Id);

                                // Send webhook notifications if configured
                                try
                                {
                                    var configurationService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
                                    var notificationService = scope.ServiceProvider.GetRequiredService<INotificationService>();
                                    var webhooks = await configurationService.GetWebhookConfigurationsAsync();
                                    foreach (var webhook in webhooks.Where(w => w.IsEnabled && w.Triggers.Contains("Moved")))
                                    {

                                        await notificationService.SendNotificationAsync(
                                            "Moved",
                                            new
                                            {
                                                AudiobookTitle = audiobook.Title,
                                                Source = source,
                                                Target = target,
                                                Timestamp = DateTime.UtcNow
                                            },
                                            webhook.Url,
                                            webhook.Triggers
                                        );
                                    }

                                    // Mark notification as sent
                                    historyEntry.NotificationSent = true;
                                    await historyRepository.UpdateAsync(historyEntry);
                                }
                                catch (Exception notifyEx) when (notifyEx is not OperationCanceledException && notifyEx is not OutOfMemoryException && notifyEx is not StackOverflowException)
                                {
                                    logger.LogWarning(notifyEx, "Failed to send move notification for {JobId}", job.Id);
                                }

                                // Send toast notification
                                try
                                {
                                    var message = !string.IsNullOrEmpty(audiobook.Title)
                                        ? $"Moved {audiobook.Title} to {target}"
                                        : $"Moved audiobook to {target}";

                                    await toastService.PublishToastAsync(
                                        "success",
                                        "Move Complete",
                                        message,
                                        timeoutMs: 5000);

                                    logger.LogDebug("Sent toast notification for move job {JobId}", job.Id);
                                }
                                catch (Exception toastEx) when (toastEx is not OperationCanceledException && toastEx is not OutOfMemoryException && toastEx is not StackOverflowException)
                                {
                                    logger.LogDebug(toastEx, "Failed to send toast notification for move job {JobId}", job.Id);
                                }

                                // Enqueue a scan job and broadcast an immediate AudiobookUpdate so detail views update promptly
                                try
                                {
                                    var scanJobId = await scanQueueService.EnqueueScanAsync(audiobook, null);
                                    logger.LogInformation("Enqueued scan job {ScanJobId} for audiobook {AudiobookId} after move", scanJobId, audiobook.Id);

                                    // Load latest audiobook state and broadcast a full DTO so clients can update instantly without fetching
                                    try
                                    {
                                        var fresh = await audiobookRepository.GetByIdAsync(audiobook.Id);
                                        if (fresh != null)
                                        {
                                            var audiobookDtoFull = AudiobookDtoFactory.BuildFromEntity(fresh);
                                            await hubContext.Clients.All.SendAsync("AudiobookUpdate", audiobookDtoFull);
                                            logger.LogInformation("Broadcasted full AudiobookUpdate for AudiobookId {AudiobookId} after move job {JobId}", audiobook.Id, job.Id);
                                        }
                                    }
                                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                                    {
                                        logger.LogWarning(ex, "Failed to broadcast full AudiobookUpdate for AudiobookId {AudiobookId} after move job {JobId}", audiobook.Id, job.Id);
                                    }
                                }
                                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                                {
                                    logger.LogWarning(ex, "Failed to enqueue scan or broadcast AudiobookUpdate after move job {JobId}", job.Id);
                                }
                            }
                            catch (Exception historyEx) when (historyEx is not OperationCanceledException && historyEx is not OutOfMemoryException && historyEx is not StackOverflowException)
                            {
                                logger.LogWarning(historyEx, "Failed to add history entry or send notifications for move job {JobId}", job.Id);
                            }

                            moveQueueService.UpdateJobStatus(job.Id, "Completed");
                            logger.LogInformation("Move job {JobId} completed: {Source} -> {Target}", job.Id, LogRedaction.SanitizeFilePath(source), LogRedaction.SanitizeFilePath(target));
                            // Completed move job — status updated and broadcasted where configured
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            // MoveExecutor cleans up its own staging dir on
                            // its internal failure path, but if the throw came
                            // from somewhere else (BasePath update, history,
                            // etc.) the temp dir may still exist — try once.
                            try
                            {
                                if (!string.IsNullOrEmpty(attemptedTempPath) && Directory.Exists(attemptedTempPath))
                                {
                                    Directory.Delete(attemptedTempPath, true);
                                }
                            }
                            catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException)
                            {
                                System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                            }

                            // Cap the surfaced error message so a pathologically
                            // long error (deeply nested path) doesn't blow up
                            // History / toast payloads.
                            var failureMessage = MoveExecutor.TruncateErrorMessage(ex.Message);

                            // Increment attempt count for the job on failure
                            try
                            {
                                var dbJob = await moveJobRepository.GetByIdAsync(job.Id, stoppingToken);
                                if (dbJob != null)
                                {
                                    dbJob.AttemptCount += 1;
                                    await moveJobRepository.UpdateAsync(dbJob, stoppingToken);
                                }
                            }
                            catch (Exception attEx) when (attEx is not OperationCanceledException && attEx is not OutOfMemoryException && attEx is not StackOverflowException)
                            {
                                logger.LogWarning(attEx, "Failed to increment AttemptCount for job {JobId} after failure", job.Id);
                            }

                            // Record failure in history and send a toast notification
                            try
                            {
                                var historyEntry = new History
                                {
                                    AudiobookId = audiobook.Id,
                                    AudiobookTitle = audiobook.Title,
                                    EventType = "MoveFailed",
                                    Message = $"Move failed: {failureMessage}",
                                    Source = "Move",
                                    Timestamp = DateTime.UtcNow,
                                    NotificationSent = false,
                                    Data = System.Text.Json.JsonSerializer.Serialize(new { JobId = job.Id, Error = failureMessage })
                                };

                                var historyRepository = scope.ServiceProvider.GetRequiredService<IHistoryRepository>();
                                await historyRepository.AddAsync(historyEntry);
                                logger.LogInformation("Added history entry for failed move job {JobId}", job.Id);

                                try
                                {
                                    var message = !string.IsNullOrEmpty(audiobook.Title)
                                        ? $"Failed to move {audiobook.Title}: {failureMessage}"
                                        : $"Move failed: {failureMessage}";

                                    await toastService.PublishToastAsync("error", "Move Failed", message, timeoutMs: 15000);
                                    logger.LogDebug("Sent toast notification for failed move job {JobId}", job.Id);
                                }
                                catch (Exception toastEx) when (toastEx is not OperationCanceledException && toastEx is not OutOfMemoryException && toastEx is not StackOverflowException)
                                {
                                    logger.LogDebug(toastEx, "Failed to send toast notification for failed move job {JobId}", job.Id);
                                }
                            }
                            catch (Exception historyEx) when (historyEx is not OperationCanceledException && historyEx is not OutOfMemoryException && historyEx is not StackOverflowException)
                            {
                                logger.LogWarning(historyEx, "Failed to add history entry for failed move job {JobId}", job.Id);
                            }

                            moveQueueService.UpdateJobStatus(job.Id, "Failed", failureMessage);
                            logger.LogError(ex, "Move job {JobId} failed", job.Id);
                            // Failure during move job — attempt counts updated and history recorded where configured
                        }
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                    catch (OperationCanceledException ex)
                    {
                        logger.LogWarning(ex, "Move job {JobId} canceled/timed out", job.Id);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        logger.LogError(ex, "Unexpected error processing move job {JobId}", job.Id);
                        try { moveQueueService.UpdateJobStatus(job.Id, "Failed", MoveExecutor.TruncateErrorMessage(ex.Message)); }
                        catch (Exception caughtEx_2) when (caughtEx_2 is not OperationCanceledException && caughtEx_2 is not OutOfMemoryException && caughtEx_2 is not StackOverflowException)
                        {
                            System.Diagnostics.Debug.WriteLine("Suppressed non-fatal exception in catch block.");
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                logger.LogInformation("MoveBackgroundService stopping due to host shutdown");
            }
            catch (OperationCanceledException ex)
            {
                logger.LogWarning(ex, "MoveBackgroundService channel stream canceled/timed out");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogError(ex, "Unhandled error in MoveBackgroundService channel loop");
            }
        }

        private static string CombineWithOptionalBase(string? basePath, string candidatePath)
        {
            var normalizedPath = candidatePath.Trim();

            if (string.IsNullOrEmpty(normalizedPath))
            {
                return normalizedPath;
            }

            if (Path.IsPathRooted(normalizedPath) || string.IsNullOrWhiteSpace(basePath))
            {
                return normalizedPath;
            }

            var relativePath = normalizedPath.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (Path.IsPathRooted(relativePath))
            {
                return relativePath;
            }

            var normalizedBasePath = basePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.IsNullOrEmpty(normalizedBasePath)
                ? relativePath
                : normalizedBasePath + Path.DirectorySeparatorChar + relativePath;
        }
    }
}



