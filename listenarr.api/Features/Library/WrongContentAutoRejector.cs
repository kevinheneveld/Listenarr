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

using Listenarr.Application.Audiobooks.Verification;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Library
{
    /// <summary>
    /// Runs the same "Not an audiobook" flow the manual button uses, on behalf of
    /// the verification background service when an on-import verdict is a
    /// confident credit-backed Mismatch. Adds an explicit history entry so the
    /// automatic action is auditable.
    /// </summary>
    public sealed class WrongContentAutoRejector : IWrongContentAutoRejector
    {
        private readonly LibraryNotAudiobookWorkflow _workflow;
        private readonly IHistoryRepository _historyRepository;
        private readonly ILogger<WrongContentAutoRejector> _logger;

        public WrongContentAutoRejector(
            LibraryNotAudiobookWorkflow workflow,
            IHistoryRepository historyRepository,
            ILogger<WrongContentAutoRejector> logger)
        {
            _workflow = workflow;
            _historyRepository = historyRepository;
            _logger = logger;
        }

        public async Task<bool> TryRejectAsync(int audiobookId, CancellationToken ct = default)
        {
            var result = await _workflow.RejectAsync(audiobookId, ct);
            var success = result is OkObjectResult;

            if (success)
            {
                try
                {
                    await _historyRepository.AddAsync(new History
                    {
                        AudiobookId = audiobookId,
                        EventType = "Auto-rejected wrong content",
                        Message = "On-import verification heard credits naming a different book at high confidence; files purged, release blocklisted, re-search queued.",
                        Source = "verification-auto-reject",
                        Timestamp = DateTime.UtcNow
                    }, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "auto-reject: failed to record history for audiobook {AudiobookId} (non-critical)", audiobookId);
                }
            }
            else
            {
                _logger.LogWarning("auto-reject: not-audiobook flow did not succeed for audiobook {AudiobookId}", audiobookId);
            }

            return success;
        }
    }
}
