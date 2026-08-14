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

using Listenarr.Application.Search;
using Listenarr.Application.Search.Contracts;
using Listenarr.Domain.Search;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    /// <summary>
    /// "Not an audiobook" / wrong content: the content imported for this book
    /// is the wrong thing (e.g. a music single that matched the title).
    /// Removes every tracked file from disk and the library, blocklists the
    /// delivering release(s) so the re-search cannot immediately re-grab the
    /// same junk, resets the (now stale) verification verdict, keeps the book
    /// monitored, and kicks off a background search for a better copy.
    /// </summary>
    public sealed class LibraryNotAudiobookWorkflow
    {
        private readonly IAudiobookRepository _repo;
        private readonly IAudiobookFileRepository _audioFileRepository;
        private readonly IAudiobookFileService _audiobookFileService;
        private readonly IHistoryRepository _historyRepository;
        private readonly IBlockedReleaseRepository _blockedReleaseRepository;
        private readonly IDownloadRepository _downloadRepository;
        private readonly DashboardAggregateCache _aggregateCache;
        private readonly IAutomaticSearchInvoker? _searchInvoker;
        private readonly ILogger<LibraryNotAudiobookWorkflow> _logger;

        public LibraryNotAudiobookWorkflow(
            IAudiobookRepository repo,
            IAudiobookFileRepository audioFileRepository,
            IAudiobookFileService audiobookFileService,
            IHistoryRepository historyRepository,
            IBlockedReleaseRepository blockedReleaseRepository,
            IDownloadRepository downloadRepository,
            DashboardAggregateCache aggregateCache,
            ILogger<LibraryNotAudiobookWorkflow> logger,
            IAutomaticSearchInvoker? searchInvoker = null)
        {
            _repo = repo;
            _audioFileRepository = audioFileRepository;
            _audiobookFileService = audiobookFileService;
            _historyRepository = historyRepository;
            _blockedReleaseRepository = blockedReleaseRepository;
            _downloadRepository = downloadRepository;
            _aggregateCache = aggregateCache;
            _searchInvoker = searchInvoker;
            _logger = logger;
        }

        public async Task<IActionResult> RejectAsync(int id, CancellationToken ct)
        {
            var audiobook = await _repo.GetByIdAsync(id);
            if (audiobook == null)
            {
                return new NotFoundObjectResult(new { message = "Audiobook not found" });
            }

            var filesRemoved = 0;
            var warnings = new List<string>();

            try
            {
                var files = await _audioFileRepository.GetByAudiobookIdAsync(id, ct);
                foreach (var file in files)
                {
                    try
                    {
                        var result = await _audiobookFileService.DeleteAudiobookFileAsync(
                            audiobook, file.Id, deleteFromDisk: true, source: "not-audiobook", ct: ct);
                        if (result.Outcome == DeleteAudiobookFileOutcome.Deleted)
                        {
                            filesRemoved++;
                        }
                        if (result.Warnings != null)
                        {
                            warnings.AddRange(result.Warnings);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        warnings.Add($"Could not remove file id {file.Id}.");
                        _logger.LogWarning(ex, "not-audiobook: failed to remove AudiobookFile {FileId} for audiobook {AudiobookId}", file.Id, id);
                    }
                }

                // A monitored book stays tracked so the re-search can fill it with
                // the correct release — but a deliberately UNmonitored book must
                // stay unmonitored. Forcing true here let the wrong-content
                // auto-rejector silently re-activate books benched against
                // title-fallback grab loops whenever a straggler import for them
                // was rejected.

                // Any verification verdict described the audio that was just deleted —
                // without this reset the empty record keeps wearing a stale "Needs
                // review" badge and a transcript of content that no longer exists.
                // Unconditional: this action is itself the human ruling that the
                // content was wrong. The replacement import re-verifies via
                // verify-on-import.
                audiobook.VerificationStatus = VerificationStatus.Unverified;
                audiobook.VerificationConfidence = null;
                audiobook.VerifiedAt = null;
                audiobook.VerifiedBy = null;
                audiobook.VerificationMethod = null;
                audiobook.VerificationTranscript = null;
                audiobook.VerificationDetailJson = null;

                await _repo.UpdateAsync(audiobook);

                try
                {
                    await _historyRepository.AddAsync(new History
                    {
                        AudiobookId = audiobook.Id,
                        AudiobookTitle = audiobook.Title ?? "Unknown Title",
                        EventType = "Rejected",
                        Message = $"Marked wrong content: removed {filesRemoved} file(s) and started a new search.",
                        Source = "not-audiobook",
                        Timestamp = DateTime.UtcNow
                    }, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "not-audiobook: failed to record history for audiobook {AudiobookId} (non-critical)", id);
                }

                // Blocklist the releases that delivered the rejected content, or the
                // re-search below would immediately re-grab the exact same release
                // (live loops: a 15-book collection re-imported after every cleanup,
                // and a mislabeled wrong-book release likewise). Best-effort.
                try
                {
                    var bookDownloads = await _downloadRepository.GetByAudiobookIdAsync(id, ct);
                    var deliveredReleases = bookDownloads
                        .Where(d => d.Status is DownloadStatus.Moved or DownloadStatus.Completed)
                        .Where(d => !string.IsNullOrWhiteSpace(d.Title))
                        .OrderByDescending(d => d.CompletedAt ?? d.StartedAt)
                        .Take(5)
                        .Select(d => (Title: d.Title!, Hash: d.GetMetadataString("TorrentHash")))
                        .ToList();

                    // The delivering Download row is often GONE by rejection
                    // time — the deferred-removal cleanup deletes it once the
                    // client item is removed — leaving nothing to blocklist,
                    // and the re-search re-grabs the identical release (live
                    // loop: 'The Lost Boys (2021) - Faye Kellerman' re-grabbed
                    // after every one of six rejections, zero blocklist rows).
                    // History outlives the row: fold in the recent
                    // DownloadCompleted source titles too.
                    var completedTitles = (await _historyRepository.GetByAudiobookIdAsync(id, ct))
                        .Where(h => h.EventType == "DownloadCompleted" && !string.IsNullOrWhiteSpace(h.SourceTitle))
                        .OrderByDescending(h => h.Timestamp)
                        .Select(h => h.SourceTitle!)
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(5);
                    foreach (var historyTitle in completedTitles)
                    {
                        if (!deliveredReleases.Any(r => string.Equals(r.Title, historyTitle, StringComparison.OrdinalIgnoreCase)))
                        {
                            deliveredReleases.Add((historyTitle, null));
                        }
                    }

                    var alreadyBlocked = await _blockedReleaseRepository.GetByAudiobookIdAsync(id, ct);
                    var added = 0;
                    foreach (var delivered in deliveredReleases)
                    {
                        if (Listenarr.Application.Search.BlockedReleaseMatcher.IsBlocked(
                                delivered.Title, delivered.Hash, alreadyBlocked))
                        {
                            continue;
                        }
                        await _blockedReleaseRepository.AddAsync(new BlockedRelease
                        {
                            AudiobookId = id,
                            ReleaseTitle = delivered.Title,
                            TorrentHash = delivered.Hash,
                            Reason = "Rejected via 'Wrong content'"
                        }, ct);
                        added++;
                    }
                    if (added > 0)
                    {
                        _logger.LogInformation(
                            "Blocklisted {Count} release(s) for audiobook {AudiobookId} after wrong-content rejection",
                            added, id);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "not-audiobook: failed to blocklist source releases for audiobook {AudiobookId}", id);
                }

                // Kick off a fresh search in the BACKGROUND. Awaiting it would walk
                // every indexer inside the request and could outlive proxy/client
                // timeouts — the UI then reports "Action failed" on an action that
                // fully succeeded. The invoker creates its own scope, so it safely
                // outlives this request; failures only mean the next automatic
                // cycle picks the book up instead.
                var searchStarted = false;
                if (_searchInvoker != null && audiobook.Monitored)
                {
                    searchStarted = true;
                    var searchTask = _searchInvoker.SearchAudiobookNowAsync(id, CancellationToken.None);
                    _ = searchTask.ContinueWith(
                        t => _logger.LogWarning(t.Exception, "not-audiobook: background search failed for audiobook {AudiobookId} (will retry on next cycle)", id),
                        TaskContinuationOptions.OnlyOnFaulted);
                }

                _aggregateCache.InvalidateAll(); // swept books must vanish from cached music-candidates/stats
                return new OkObjectResult(new
                {
                    message = "Removed the rejected content and started a background search",
                    id,
                    filesRemoved,
                    searchStarted,
                    warnings
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "not-audiobook: failed for audiobook {AudiobookId}", id);
                return new ObjectResult(new { message = "Failed to mark as not an audiobook" })
                {
                    StatusCode = StatusCodes.Status500InternalServerError
                };
            }
        }
    }
}
