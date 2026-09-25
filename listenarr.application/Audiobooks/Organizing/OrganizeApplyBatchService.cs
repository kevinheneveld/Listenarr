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

namespace Listenarr.Application.Audiobooks.Organizing
{
    /// <summary>
    /// In-memory, single-batch implementation of <see cref="IOrganizeApplyBatch"/>
    /// (same shape as the wanted-search queue). A restart drops whatever was
    /// still pending; moves already handed to the durable move queue survive.
    /// </summary>
    public sealed class OrganizeApplyBatchService : IOrganizeApplyBatch
    {
        /// <summary>The snapshot carries at most this many problem rows (newest last).</summary>
        public const int MaxProblemsInSnapshot = 50;

        private readonly object _sync = new();
        private readonly ILogger<OrganizeApplyBatchService> _logger;
        private readonly TimeProvider _timeProvider;
        private readonly List<OrganizeApplyQueuedJob> _queuedJobs = [];
        private readonly List<OrganizeApplyRepointedRow> _repointedRows = [];
        private readonly List<OrganizeApplyProblem> _problems = [];

        private Channel<OrganizeApplyItem> _channel = Channel.CreateUnbounded<OrganizeApplyItem>();
        private CancellationTokenSource _batchCts = new();
        private Guid? _batchId;
        private bool _isRunning;
        private bool _cancelled;
        private int _total;
        private int _pending;
        private int _processed;
        private int _queued;
        private int _notAccepted;
        private int _failed;
        private int _repointed;
        private int? _currentAudiobookId;
        private string? _currentTitle;
        private DateTime? _startedAt;
        private DateTime? _completedAt;

        public OrganizeApplyBatchService(ILogger<OrganizeApplyBatchService> logger, TimeProvider? timeProvider = null)
        {
            _logger = logger;
            _timeProvider = timeProvider ?? TimeProvider.System;
        }

        public ChannelReader<OrganizeApplyItem> Reader
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

        public bool TryStart(IReadOnlyList<OrganizeApplyItem> items, out Guid batchId)
        {
            ArgumentNullException.ThrowIfNull(items);
            lock (_sync)
            {
                if (_isRunning)
                {
                    batchId = Guid.Empty;
                    return false;
                }

                batchId = Guid.NewGuid();
                _batchId = batchId;
                _isRunning = true;
                _cancelled = false;
                _total = 0;
                _pending = 0;
                _processed = 0;
                _queued = 0;
                _notAccepted = 0;
                _failed = 0;
                _repointed = 0;
                _currentAudiobookId = null;
                _currentTitle = null;
                _startedAt = _timeProvider.GetUtcNow().UtcDateTime;
                _completedAt = null;
                _queuedJobs.Clear();
                _repointedRows.Clear();
                _problems.Clear();

                if (_batchCts.IsCancellationRequested)
                {
                    _batchCts.Dispose();
                    _batchCts = new CancellationTokenSource();
                }
                if (_channel.Reader.Completion.IsCompleted)
                {
                    _channel = Channel.CreateUnbounded<OrganizeApplyItem>();
                }

                foreach (var item in items)
                {
                    if (_channel.Writer.TryWrite(item))
                    {
                        _total++;
                        _pending++;
                    }
                }

                _logger.LogInformation("Organize batch {BatchId}: started with {Total} move(s) to queue", batchId, _total);
                if (_total == 0)
                {
                    FinishBatchLocked();
                }
                return true;
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

                var dropped = _pending;
                _pending = 0;
                _cancelled = true;
                _channel.Writer.TryComplete();
                _channel = Channel.CreateUnbounded<OrganizeApplyItem>();
                _batchCts.Cancel();
                _logger.LogInformation("Organize batch {BatchId}: cancelled with {Dropped} move(s) still to queue", _batchId, dropped);
                FinishBatchLocked();
            }
        }

        public OrganizeApplyBatchSnapshot Snapshot()
        {
            lock (_sync)
            {
                return new OrganizeApplyBatchSnapshot(
                    _batchId,
                    _isRunning,
                    _total,
                    _processed,
                    _queued,
                    _notAccepted,
                    _failed,
                    _repointed,
                    _currentAudiobookId,
                    _currentTitle,
                    _startedAt,
                    _completedAt,
                    _cancelled,
                    _queuedJobs.ToArray(),
                    _repointedRows.ToArray(),
                    _problems.ToArray());
            }
        }

        public void MarkStarted(OrganizeApplyItem item)
        {
            lock (_sync)
            {
                if (_pending > 0) _pending--;
                _currentAudiobookId = item.AudiobookId;
                _currentTitle = item.Title;
            }
        }

        public void MarkQueued(OrganizeApplyItem item, Guid jobId)
        {
            lock (_sync)
            {
                CompleteCurrentLocked(item);
                _queued++;
                _queuedJobs.Add(new OrganizeApplyQueuedJob(jobId.ToString(), item.AudiobookId, item.Title, item.TargetPath));
            }
        }

        public void MarkRepointed(OrganizeApplyItem item)
        {
            lock (_sync)
            {
                CompleteCurrentLocked(item);
                _repointed++;
                _repointedRows.Add(new OrganizeApplyRepointedRow(item.AudiobookId, item.Title, item.TargetPath));
            }
        }

        public void MarkNotAccepted(OrganizeApplyItem item, string reason)
        {
            lock (_sync)
            {
                CompleteCurrentLocked(item);
                _notAccepted++;
                AddProblemLocked(item, reason);
            }
        }

        public void MarkFailed(OrganizeApplyItem item, string reason)
        {
            lock (_sync)
            {
                CompleteCurrentLocked(item);
                _failed++;
                AddProblemLocked(item, reason);
            }
        }

        public void MarkBatchFinishedIfDrained()
        {
            lock (_sync)
            {
                if (_isRunning && _pending == 0 && _currentAudiobookId == null)
                {
                    FinishBatchLocked();
                }
            }
        }

        private void CompleteCurrentLocked(OrganizeApplyItem item)
        {
            if (_currentAudiobookId == item.AudiobookId)
            {
                _currentAudiobookId = null;
                _currentTitle = null;
            }
            _processed++;
        }

        private void AddProblemLocked(OrganizeApplyItem item, string reason)
        {
            _problems.Add(new OrganizeApplyProblem(item.AudiobookId, item.Title, reason));
            if (_problems.Count > MaxProblemsInSnapshot)
            {
                _problems.RemoveAt(0);
            }
        }

        private void FinishBatchLocked()
        {
            _isRunning = false;
            _completedAt = _timeProvider.GetUtcNow().UtcDateTime;
            _logger.LogInformation(
                "Organize batch {BatchId}: finished — {Queued} queued, {NotAccepted} not accepted, {Failed} failed of {Total}{Cancelled}",
                _batchId, _queued, _notAccepted, _failed, _total, _cancelled ? " (cancelled)" : string.Empty);
        }
    }
}
