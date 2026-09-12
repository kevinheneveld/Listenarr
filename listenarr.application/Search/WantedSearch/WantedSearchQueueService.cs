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

using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Search.WantedSearch
{
    /// <summary>
    /// In-memory batch queue for the Wanted page. Holds only ids and counters; the
    /// hosted worker owns the per-book search. Everything mutable sits under one
    /// lock — the queue is touched from request threads and the worker concurrently.
    /// Not persisted by design: a restart simply drops the batch, and the user can
    /// re-run it from the page.
    /// </summary>
    public sealed class WantedSearchQueueService : IWantedSearchQueue
    {
        private readonly object _sync = new();
        private readonly ILogger<WantedSearchQueueService> _logger;
        private readonly TimeProvider _timeProvider;
        private readonly HashSet<int> _pendingIds = [];

        private Channel<int> _channel = Channel.CreateUnbounded<int>();
        private CancellationTokenSource _batchCts = new();

        private bool _isRunning;
        private bool _cancelled;
        private int _processed;
        private int _total;
        private int _grabbed;
        private int _failed;
        private int? _currentAudiobookId;
        // A book still in flight from a cancelled batch when a fresh one starts;
        // its completion must not bleed into the new batch's counters.
        private int? _staleInFlightId;
        private string? _currentTitle;
        private DateTime? _startedAt;
        private DateTime? _completedAt;

        public WantedSearchQueueService(
            ILogger<WantedSearchQueueService> logger,
            TimeProvider? timeProvider = null)
        {
            _logger = logger;
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        // The channel is replaced on Cancel (draining an unbounded channel in place
        // is awkward), so the worker must re-read the property each loop iteration.
        public ChannelReader<int> Reader
        {
            get
            {
                lock (_sync)
                {
                    return _channel.Reader;
                }
            }
        }

        public CancellationToken BatchToken
        {
            get
            {
                lock (_sync)
                {
                    return _batchCts.Token;
                }
            }
        }

        public WantedSearchEnqueueResult Enqueue(IReadOnlyCollection<int> audiobookIds)
        {
            ArgumentNullException.ThrowIfNull(audiobookIds);

            lock (_sync)
            {
                if (!_isRunning)
                {
                    StartFreshBatchLocked();
                }

                var accepted = 0;
                var alreadyQueued = 0;
                foreach (var id in audiobookIds)
                {
                    if (id <= 0)
                    {
                        continue;
                    }

                    if (_currentAudiobookId == id || !_pendingIds.Add(id))
                    {
                        alreadyQueued++;
                        continue;
                    }

                    // Unbounded channel: TryWrite only fails once the writer is completed,
                    // which never happens for a live batch.
                    if (_channel.Writer.TryWrite(id))
                    {
                        accepted++;
                    }
                    else
                    {
                        _pendingIds.Remove(id);
                    }
                }

                _total += accepted;
                _logger.LogInformation(
                    "Wanted search: queued {Accepted} book(s) ({AlreadyQueued} already queued); batch now {Pending} pending of {Total}",
                    accepted, alreadyQueued, _pendingIds.Count, _total);

                return new WantedSearchEnqueueResult(accepted, alreadyQueued, _pendingIds.Count, _total);
            }
        }

        public void Cancel()
        {
            lock (_sync)
            {
                if (!_isRunning)
                {
                    return;
                }

                var dropped = _pendingIds.Count;
                _pendingIds.Clear();
                _cancelled = true;

                // Swap the channel so the worker stops seeing the old ids, then
                // cancel the batch token so the book in flight is abandoned too.
                _channel.Writer.TryComplete();
                _channel = Channel.CreateUnbounded<int>();
                _batchCts.Cancel();

                _logger.LogInformation("Wanted search: cancelled with {Dropped} book(s) still pending", dropped);

                // The batch is over from the user's point of view right now; the
                // worker abandons the in-flight book on its own via the token.
                FinishBatchLocked();
            }
        }

        public WantedSearchQueueSnapshot Snapshot()
        {
            lock (_sync)
            {
                return new WantedSearchQueueSnapshot(
                    _isRunning,
                    _pendingIds.Count,
                    _processed,
                    _total,
                    _grabbed,
                    _failed,
                    _currentAudiobookId,
                    _currentTitle,
                    _startedAt,
                    _completedAt,
                    _cancelled);
            }
        }

        public void MarkStarted(int audiobookId, string? title)
        {
            lock (_sync)
            {
                _pendingIds.Remove(audiobookId);
                _currentAudiobookId = audiobookId;
                _currentTitle = title;
            }
        }

        public void MarkCompleted(int audiobookId, bool success, int downloadsQueued)
        {
            lock (_sync)
            {
                if (_staleInFlightId == audiobookId)
                {
                    _staleInFlightId = null;
                    return;
                }

                if (_currentAudiobookId == audiobookId)
                {
                    _currentAudiobookId = null;
                }

                _processed++;
                if (success)
                {
                    _grabbed += downloadsQueued;
                }
                else
                {
                    _failed++;
                }
            }
        }

        public void MarkBatchFinishedIfDrained()
        {
            lock (_sync)
            {
                if (_isRunning && _pendingIds.Count == 0 && _currentAudiobookId == null)
                {
                    FinishBatchLocked();
                }
            }
        }

        private void StartFreshBatchLocked()
        {
            _isRunning = true;
            _cancelled = false;
            _processed = 0;
            _total = 0;
            _grabbed = 0;
            _failed = 0;
            _staleInFlightId = _currentAudiobookId;
            _currentAudiobookId = null;
            _currentTitle = null;
            _startedAt = _timeProvider.GetUtcNow().UtcDateTime;
            _completedAt = null;
            _pendingIds.Clear();

            if (_batchCts.IsCancellationRequested)
            {
                _batchCts.Dispose();
                _batchCts = new CancellationTokenSource();
            }

            if (_channel.Reader.Completion.IsCompleted)
            {
                _channel = Channel.CreateUnbounded<int>();
            }
        }

        private void FinishBatchLocked()
        {
            _isRunning = false;
            _completedAt = _timeProvider.GetUtcNow().UtcDateTime;
            _logger.LogInformation(
                "Wanted search: batch finished — processed {Processed} of {Total}, grabbed {Grabbed}, failed {Failed}{Cancelled}",
                _processed, _total, _grabbed, _failed, _cancelled ? " (cancelled)" : string.Empty);
        }
    }
}
