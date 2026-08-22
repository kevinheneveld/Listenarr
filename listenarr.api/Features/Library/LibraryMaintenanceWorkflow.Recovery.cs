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

using Listenarr.Domain.Common;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    public sealed partial class LibraryMaintenanceWorkflow
    {
        /// <summary>
        /// Repair rows broken by a failed move: for each audiobook whose most recent
        /// "Moved" history event names a target that exists on disk with content, while
        /// the row itself has zero tracked files and a stale/empty BasePath, re-stamp
        /// BasePath to the target and enqueue a recovery scan. Never touches rows that
        /// currently own files.
        /// </summary>
        public async Task<IActionResult> RecoverBrokenMovesAsync(bool dryRun, CancellationToken ct)
        {
            var movedEvents = await _historyRepository.GetByEventTypeAsync("Moved", limit: null, ct);

            // Most-recent Moved event per audiobook id wins — a twice-moved book
            // recovers to the latest target.
            var latestPerAudiobook = movedEvents
                .Where(e => e.AudiobookId.HasValue && e.AudiobookId.Value > 0)
                .GroupBy(e => e.AudiobookId!.Value)
                .Select(g => new { AudiobookId = g.Key, Event = g.OrderByDescending(e => e.Timestamp).ThenByDescending(e => e.Id).First() })
                .ToList();

            var fileCountByAudiobookId = await _fileRepository.GetCountsByAudiobookIdAsync(ct);

            var recoveredEntries = new List<object>();
            var skippedEntries = new List<object>();

            foreach (var item in latestPerAudiobook)
            {
                if (ct.IsCancellationRequested) break;

                var audiobookId = item.AudiobookId;
                var ev = item.Event;

                // The move service writes History.Data as { JobId, Source, Target }.
                string? target = null;
                try
                {
                    if (!string.IsNullOrWhiteSpace(ev.Data))
                    {
                        using var doc = System.Text.Json.JsonDocument.Parse(ev.Data);
                        if (doc.RootElement.TryGetProperty("Target", out var tEl) && tEl.ValueKind == System.Text.Json.JsonValueKind.String)
                        {
                            target = tEl.GetString();
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Recovery: failed to parse History.Data for audiobook {AudiobookId}", audiobookId);
                }

                if (string.IsNullOrWhiteSpace(target))
                {
                    skippedEntries.Add(new { audiobookId, reason = "no_target_in_history" });
                    continue;
                }

                var audiobook = await _repo.GetByIdAsync(audiobookId);
                if (audiobook == null)
                {
                    skippedEntries.Add(new { audiobookId, reason = "audiobook_no_longer_exists", target });
                    continue;
                }

                var currentFileCount = fileCountByAudiobookId.GetValueOrDefault(audiobookId, 0);
                var currentBaseNorm = LibraryOrganizeSweepWorkflow.NormalizeOrganizePath(audiobook.BasePath);
                var targetNorm = LibraryOrganizeSweepWorkflow.NormalizeOrganizePath(target);

                // Only broken-state rows: zero tracked files AND (BasePath empty OR not
                // already the target). A row with files is healthy or mid-recovery.
                if (currentFileCount > 0)
                {
                    skippedEntries.Add(new { audiobookId, reason = "already_has_files", currentFileCount, basePath = audiobook.BasePath });
                    continue;
                }
                if (!string.IsNullOrEmpty(currentBaseNorm)
                    && string.Equals(currentBaseNorm, targetNorm, StringComparison.OrdinalIgnoreCase))
                {
                    skippedEntries.Add(new { audiobookId, reason = "basepath_already_matches_target", target });
                    continue;
                }

                // The target must actually exist on disk with content — a hand-moved or
                // deleted folder must not be silently re-stamped.
                var targetHasContent = _recoveryFilesystem.DirectoryExistsWithContent(target, out var targetExists);
                if (!targetExists)
                {
                    skippedEntries.Add(new { audiobookId, reason = "target_path_missing_on_disk", target });
                    continue;
                }
                if (!targetHasContent)
                {
                    skippedEntries.Add(new { audiobookId, reason = "target_path_empty", target });
                    continue;
                }

                var entry = new
                {
                    audiobookId,
                    title = audiobook.Title,
                    previousBasePath = audiobook.BasePath,
                    target,
                    movedAt = ev.Timestamp,
                };

                if (dryRun)
                {
                    recoveredEntries.Add(entry);
                    continue;
                }

                try
                {
                    audiobook.BasePath = FileUtils.NormalizeStoredPath(target);
                    await _repo.UpdateAsync(audiobook);

                    if (_scanQueueService != null)
                    {
                        var scanJobId = await _scanQueueService.EnqueueScanAsync(
                            new ScanEnqueueCommand(
                                audiobook,
                                Path: target,
                                AuthorizationMode: ScanAuthorizationMode.PreauthorizedPath,
                                SkipMissingBasePathCleanup: true));
                        _logger.LogInformation("Recovery: enqueued scan {ScanJobId} for audiobook {AudiobookId} at recovered BasePath {Target}", scanJobId, audiobookId, LogRedaction.SanitizeFilePath(target));
                    }

                    recoveredEntries.Add(entry);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogError(ex, "Recovery: failed to recover audiobook {AudiobookId} (target {Target})", audiobookId, LogRedaction.SanitizeFilePath(target));
                    skippedEntries.Add(new { audiobookId, reason = "recovery_failed", error = ex.Message, target });
                }
            }

            return new OkObjectResult(new
            {
                dryRun,
                inspected = latestPerAudiobook.Count,
                recovered = recoveredEntries.Count,
                skipped = skippedEntries.Count,
                recoveredDetails = recoveredEntries,
                skippedDetails = skippedEntries,
            });
        }

        /// <summary>
        /// Repair rows whose BasePath is a bare library root: compute the canonical
        /// target (same planner as add/move/organize) or find the real folder via a
        /// one-pass disk index, re-stamp BasePath, and enqueue a recovery scan.
        /// </summary>
        public async Task<IActionResult> RecoverRootBaseAudiobooksAsync(bool dryRun, CancellationToken ct)
        {
            var settings = await _configurationService.GetApplicationSettingsAsync();
            var rootFolders = await _rootFolderService.GetAllAsync();

            var allAudiobooks = await _repo.GetAllAsync();
            var fileCountByAudiobookId = await _fileRepository.GetCountsByAudiobookIdAsync(ct);

            var recoveredEntries = new List<object>();
            var skippedEntries = new List<object>();
            var inspected = 0;

            var folderIndex = _recoveryFilesystem.BuildFolderIndex(
                rootFolders.Where(r => !string.IsNullOrWhiteSpace(r.Path)).Select(r => r.Path!).ToList());
            _logger.LogInformation(
                "Root-base recovery: indexed {Count} audio-containing folders across {RootCount} root folder(s) before scanning broken rows.",
                folderIndex.IndexedFolders, rootFolders.Count);

            foreach (var audiobook in allAudiobooks)
            {
                if (ct.IsCancellationRequested) break;

                if (!LibraryOrganizeSweepWorkflow.IsSourceAtRootFolder(audiobook.BasePath, rootFolders))
                {
                    continue;
                }

                inspected++;

                var (target, invalidReason, _) = _organizeSweep.ComputeOrganizeTarget(audiobook, settings, rootFolders);
                if (!string.IsNullOrEmpty(invalidReason) || string.IsNullOrWhiteSpace(target))
                {
                    skippedEntries.Add(new { audiobookId = audiobook.Id, title = audiobook.Title, reason = "cannot_compute_target", detail = invalidReason });
                    continue;
                }

                var targetHasContent = _recoveryFilesystem.DirectoryExistsWithContent(target, out var targetExists);
                if (!targetExists || !targetHasContent)
                {
                    // Fall back to the disk index: one unambiguous folder match wins.
                    var author = audiobook.Authors?.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a));
                    var candidates = (string.IsNullOrWhiteSpace(audiobook.Title) || string.IsNullOrWhiteSpace(author))
                        ? Array.Empty<string>() as IReadOnlyList<string>
                        : folderIndex.Lookup(author!, audiobook.Title!);

                    if (candidates.Count == 1)
                    {
                        target = candidates[0];
                    }
                    else
                    {
                        skippedEntries.Add(new
                        {
                            audiobookId = audiobook.Id,
                            title = audiobook.Title,
                            reason = candidates.Count == 0 ? "no_folder_match_under_author" : "ambiguous_folder_match",
                            target,
                            searchDetail = candidates.Count > 1 ? string.Join(" | ", candidates.Take(5)) : null,
                        });
                        continue;
                    }
                }

                var currentFileCount = fileCountByAudiobookId.GetValueOrDefault(audiobook.Id, 0);
                if (currentFileCount > 0)
                {
                    skippedEntries.Add(new { audiobookId = audiobook.Id, title = audiobook.Title, reason = "already_has_files_at_root_basepath", currentFileCount });
                    continue;
                }

                var entry = new { audiobookId = audiobook.Id, title = audiobook.Title, previousBasePath = audiobook.BasePath, target };

                if (dryRun)
                {
                    recoveredEntries.Add(entry);
                    continue;
                }

                try
                {
                    audiobook.BasePath = FileUtils.NormalizeStoredPath(target);
                    await _repo.UpdateAsync(audiobook);

                    if (_scanQueueService != null)
                    {
                        var scanJobId = await _scanQueueService.EnqueueScanAsync(
                            new ScanEnqueueCommand(
                                audiobook,
                                Path: target,
                                AuthorizationMode: ScanAuthorizationMode.PreauthorizedPath,
                                SkipMissingBasePathCleanup: true));
                        _logger.LogInformation(
                            "Root-base recovery: enqueued scan {ScanJobId} for audiobook {AudiobookId} at recovered BasePath {Target}",
                            scanJobId, audiobook.Id, LogRedaction.SanitizeFilePath(target));
                    }

                    recoveredEntries.Add(entry);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogError(ex, "Root-base recovery: failed to recover audiobook {AudiobookId} (target {Target})", audiobook.Id, LogRedaction.SanitizeFilePath(target));
                    skippedEntries.Add(new { audiobookId = audiobook.Id, title = audiobook.Title, reason = "recovery_failed", error = ex.Message, target });
                }
            }

            return new OkObjectResult(new
            {
                dryRun,
                inspected,
                recovered = recoveredEntries.Count,
                skipped = skippedEntries.Count,
                recoveredDetails = recoveredEntries,
                skippedDetails = skippedEntries,
            });
        }

        /// <summary>
        /// Re-register rows whose BasePath exists with audio on disk but which track
        /// zero AudiobookFile rows (orphaned tracking) — enqueues a recovery scan per row.
        /// </summary>
        public async Task<IActionResult> RecoverOrphanedTrackingAsync(bool dryRun, CancellationToken ct)
        {
            var rootFolders = await _rootFolderService.GetAllAsync();
            var allAudiobooks = await _repo.GetAllAsync();
            var fileCountByAudiobookId = await _fileRepository.GetCountsByAudiobookIdAsync(ct);

            var recoveredEntries = new List<object>();
            var skippedEntries = new List<object>();
            var inspected = 0;

            foreach (var audiobook in allAudiobooks)
            {
                if (ct.IsCancellationRequested) break;

                var basePath = audiobook.BasePath;
                if (string.IsNullOrWhiteSpace(basePath)) continue;
                if (LibraryOrganizeSweepWorkflow.IsSourceAtRootFolder(basePath, rootFolders)) continue;
                if (fileCountByAudiobookId.GetValueOrDefault(audiobook.Id, 0) > 0) continue;

                inspected++;

                var audioFileCount = _recoveryFilesystem.CountAudioFiles(basePath, out var dirExists);

                if (!dirExists)
                {
                    skippedEntries.Add(new { audiobookId = audiobook.Id, title = audiobook.Title, reason = "basepath_missing_on_disk", basePath });
                    continue;
                }
                if (audioFileCount == 0)
                {
                    skippedEntries.Add(new { audiobookId = audiobook.Id, title = audiobook.Title, reason = "basepath_has_no_audio", basePath });
                    continue;
                }

                var entry = new { audiobookId = audiobook.Id, title = audiobook.Title, basePath, audioFileCount };

                if (dryRun)
                {
                    recoveredEntries.Add(entry);
                    continue;
                }

                try
                {
                    if (_scanQueueService != null)
                    {
                        var scanJobId = await _scanQueueService.EnqueueScanAsync(
                            new ScanEnqueueCommand(
                                audiobook,
                                Path: basePath,
                                AuthorizationMode: ScanAuthorizationMode.PreauthorizedPath,
                                SkipMissingBasePathCleanup: true));
                        _logger.LogInformation(
                            "Orphaned-tracking recovery: enqueued scan {ScanJobId} for audiobook {AudiobookId} at BasePath {BasePath}",
                            scanJobId, audiobook.Id, LogRedaction.SanitizeFilePath(basePath));
                    }

                    recoveredEntries.Add(entry);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogError(ex, "Orphaned-tracking recovery: failed to enqueue scan for audiobook {AudiobookId} ({BasePath})", audiobook.Id, LogRedaction.SanitizeFilePath(basePath));
                    skippedEntries.Add(new { audiobookId = audiobook.Id, title = audiobook.Title, reason = "scan_enqueue_failed", error = ex.Message, basePath });
                }
            }

            return new OkObjectResult(new
            {
                dryRun,
                inspected,
                recovered = recoveredEntries.Count,
                skipped = skippedEntries.Count,
                recoveredDetails = recoveredEntries,
                skippedDetails = skippedEntries,
            });
        }
    }
}
