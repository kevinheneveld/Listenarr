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
using System.Collections.Concurrent;
using System.Threading.Channels;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Application.Notification;
using Listenarr.Application.Security;
using Listenarr.Domain.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;


namespace Listenarr.Application.Audiobooks
{
    public class MoveQueueService : IMoveQueueService
    {
        private readonly ConcurrentDictionary<Guid, MoveJob> _jobs = new();
        private readonly Channel<MoveJob> _channel = Channel.CreateUnbounded<MoveJob>();
        private readonly ILogger<MoveQueueService> _logger;
        private readonly IServiceScopeFactory _scopeFactory;

        public MoveQueueService(ILogger<MoveQueueService> logger, IServiceScopeFactory scopeFactory)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        }

        public ChannelReader<MoveJob> Reader => _channel.Reader;

        public async Task<Guid> EnqueueMoveAsync(int audiobookId, string requestedPath, string? sourcePath = null, bool replaceStubTarget = false)
        {
            try
            {
                // Check DB for existing active job
                using var scope = _scopeFactory.CreateScope();
                var moveJobRepository = scope.ServiceProvider.GetRequiredService<IMoveJobRepository>();

                var requestedLower = (requestedPath ?? string.Empty).ToLower();
                var activeJobs = await moveJobRepository.GetByStatusAsync(new[] { "Queued", "Processing" });
                var existingDb = activeJobs.FirstOrDefault(j => j.AudiobookId == audiobookId &&
                    ((j.RequestedPath ?? string.Empty).ToLower() == requestedLower));

                if (existingDb != null)
                {
                    _jobs[existingDb.Id] = existingDb;
                    _logger.LogInformation("Found active move job {JobId} for audiobook {AudiobookId} to {Path}; deduping and returning existing job id", existingDb.Id, audiobookId, LogRedaction.SanitizeFilePath(requestedPath));
                    return existingDb.Id;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed during dedupe check for move job; will enqueue new job");
            }

            var job = new MoveJob { AudiobookId = audiobookId, RequestedPath = requestedPath, EnqueuedAt = DateTime.UtcNow, Status = "Queued", SourcePath = sourcePath, ReplaceStubTarget = replaceStubTarget };

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var moveJobRepository = scope.ServiceProvider.GetRequiredService<IMoveJobRepository>();
                await moveJobRepository.AddAsync(job);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to persist move job to database; proceeding with in-memory job");
            }

            _jobs[job.Id] = job;
            _logger.LogInformation("Enqueueing move job {JobId} for audiobook {AudiobookId} to {Path}", job.Id, audiobookId, LogRedaction.SanitizeFilePath(requestedPath));
            await _channel.Writer.WriteAsync(job);
            return job.Id;
        }

        public bool TryGetJob(Guid id, out MoveJob? job)
        {
            if (_jobs.TryGetValue(id, out job)) return true;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var moveJobRepository = scope.ServiceProvider.GetRequiredService<IMoveJobRepository>();
                job = moveJobRepository.GetByIdAsync(id).GetAwaiter().GetResult();
                if (job != null) _jobs[id] = job;
                return job != null;
            }
            catch (Exception caughtEx_1) when (caughtEx_1 is not OperationCanceledException && caughtEx_1 is not OutOfMemoryException && caughtEx_1 is not StackOverflowException)
            {
                job = null;
                return false;
            }
        }

        public void UpdateJobStatus(Guid id, string status, string? error = null)
        {
            if (_jobs.TryGetValue(id, out var job) && job != null)
            {
                job.Status = status;
                job.Error = error;
                job.UpdatedAt = DateTime.UtcNow;
                _jobs[id] = job;
            }

            try
            {
                using var scope = _scopeFactory.CreateScope();
                var moveJobRepository = scope.ServiceProvider.GetRequiredService<IMoveJobRepository>();
                var dbJob = moveJobRepository.GetByIdAsync(id).GetAwaiter().GetResult();
                if (dbJob != null)
                {
                    dbJob.Status = status;
                    dbJob.Error = error;
                    dbJob.UpdatedAt = DateTime.UtcNow;
                    moveJobRepository.UpdateAsync(dbJob).GetAwaiter().GetResult();
                }

                // Broadcast status update to SignalR clients so UI can react to Processing/Failed/Completed
                try
                {
                    var hub = scope.ServiceProvider.GetRequiredService<IHubContext<DownloadHub>>();
                    var payload = new
                    {
                        jobId = id.ToString(),
                        audiobookId = dbJob?.AudiobookId ?? (job != null ? job.AudiobookId : (int?)null),
                        status = status,
                        error = error,
                        target = dbJob?.RequestedPath ?? (job != null ? job.RequestedPath : null),
                        updatedAt = DateTime.UtcNow
                    };
                    // Fire and forget but block briefly to surface errors during development
                    hub.Clients.All.SendAsync("MoveJobUpdate", payload).GetAwaiter().GetResult();
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to broadcast MoveJobUpdate for job {JobId}", id);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to persist move job status change for {JobId}", id);
            }

            // Log error prominently if status is Failed
            if (status == "Failed" && !string.IsNullOrWhiteSpace(error))
            {
                _logger.LogError("Move job {JobId} FAILED with error: {Error}", id, error);
            }
            else
            {
                _logger.LogInformation("Updated move job {JobId} status to {Status}", id, status);
            }
        }

        public async Task<Guid?> RequeueMoveAsync(Guid jobId)
        {
            MoveJob? job = null;
            if (!_jobs.TryGetValue(jobId, out job))
            {
                try
                {
                    using var scope = _scopeFactory.CreateScope();
                    var moveJobRepository = scope.ServiceProvider.GetRequiredService<IMoveJobRepository>();
                    job = await moveJobRepository.GetByIdAsync(jobId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    _logger.LogWarning(ex, "Failed to read move job from DB while requeueing {JobId}", jobId);
                }

                if (job == null)
                {
                    _logger.LogWarning("Attempted to requeue unknown move job {JobId}", jobId);
                    return null;
                }
            }

            if (!CanRequeueJobStatus(job.Status))
            {
                _logger.LogInformation("Move job {JobId} has status {Status} and cannot be requeued", jobId, job.Status);
                return null;
            }

            var newJob = new MoveJob { AudiobookId = job.AudiobookId, RequestedPath = job.RequestedPath, EnqueuedAt = DateTime.UtcNow, Status = "Queued", SourcePath = job.SourcePath, ReplaceStubTarget = job.ReplaceStubTarget };
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var moveJobRepository = scope.ServiceProvider.GetRequiredService<IMoveJobRepository>();
                await moveJobRepository.AddAsync(newJob);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to persist requeued move job to database; proceeding with in-memory job");
            }

            _jobs[newJob.Id] = newJob;
            _logger.LogInformation("Requeueing move job {OldJobId} as new job {NewJobId} for audiobook {AudiobookId}", jobId, newJob.Id, job.AudiobookId);
            await _channel.Writer.WriteAsync(newJob);
            return newJob.Id;
        }

        private static bool CanRequeueJobStatus(string status)
        {
            return string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, "Queued", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<int> RehydratePendingAsync(TimeSpan staleProcessingThreshold, CancellationToken ct = default)
        {
            int reEnqueued = 0;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var moveJobRepository = scope.ServiceProvider.GetRequiredService<IMoveJobRepository>();
                var pending = await moveJobRepository.GetByStatusAsync(new[] { "Queued", "Processing" }, ct);

                var cutoff = DateTime.UtcNow - staleProcessingThreshold;
                foreach (var job in pending)
                {
                    if (ct.IsCancellationRequested) break;

                    // Skip jobs this process already knows about — the channel is
                    // also fresh after restart, so anything in _jobs was added by
                    // a concurrent enqueue path during startup (rare but possible
                    // under crash-restart-with-active-callers); honour idempotency.
                    if (_jobs.ContainsKey(job.Id)) continue;

                    // Stale Processing rows have no live worker (the previous
                    // process died mid-copy). Flip to Queued so the consumer
                    // treats them as fresh work, not as already-in-progress.
                    if (string.Equals(job.Status, "Processing", StringComparison.OrdinalIgnoreCase))
                    {
                        var age = job.UpdatedAt.HasValue ? DateTime.UtcNow - job.UpdatedAt.Value : DateTime.UtcNow - job.EnqueuedAt;
                        if (age < staleProcessingThreshold)
                        {
                            // Still recent — possible the previous run is in a
                            // grace-period shutdown copy that hasn't actually
                            // ended. Skip; the next call to rehydrate will pick
                            // it up if it's truly orphaned.
                            continue;
                        }
                        try
                        {
                            job.Status = "Queued";
                            job.UpdatedAt = DateTime.UtcNow;
                            await moveJobRepository.UpdateAsync(job, ct);
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                        {
                            _logger.LogWarning(ex, "Failed to flip stale Processing job {JobId} to Queued during rehydration; skipping", job.Id);
                            continue;
                        }
                    }

                    _jobs[job.Id] = job;
                    try
                    {
                        await _channel.Writer.WriteAsync(job, ct);
                        reEnqueued++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Failed to write rehydrated job {JobId} to channel", job.Id);
                        // Remove from _jobs so a later retry can re-add it.
                        _jobs.TryRemove(job.Id, out _);
                    }
                }

                if (reEnqueued > 0)
                {
                    _logger.LogInformation("MoveQueueService rehydration: re-channeled {Count} pending job(s) (Queued + stale Processing>{Threshold})", reEnqueued, staleProcessingThreshold);
                }
                else
                {
                    _logger.LogDebug("MoveQueueService rehydration: no pending jobs to re-channel (scanned {Scanned})", pending.Count);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "MoveQueueService rehydration failed");
            }
            return reEnqueued;
        }

        public async Task<int> CancelStalePendingAsync(TimeSpan staleThreshold, CancellationToken ct = default)
        {
            int cancelled = 0;
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var moveJobRepository = scope.ServiceProvider.GetRequiredService<IMoveJobRepository>();
                var pending = await moveJobRepository.GetByStatusAsync(new[] { "Queued", "Processing" }, ct);
                var cutoff = DateTime.UtcNow - staleThreshold;

                foreach (var job in pending)
                {
                    if (ct.IsCancellationRequested) break;
                    var lastTouched = job.UpdatedAt ?? job.EnqueuedAt;
                    if (lastTouched > cutoff) continue;

                    try
                    {
                        job.Status = "Cancelled";
                        job.Error = "Cancelled by operator (stale pending sweep)";
                        job.UpdatedAt = DateTime.UtcNow;
                        await moveJobRepository.UpdateAsync(job, ct);
                        // Also drop from in-memory map so a re-queue from the
                        // modal isn't deduped against this cancelled row.
                        _jobs.TryRemove(job.Id, out _);
                        cancelled++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogWarning(ex, "Failed to cancel stale move job {JobId}; skipping", job.Id);
                    }
                }

                _logger.LogInformation("Cancelled {Count} stale move job(s) older than {Threshold}", cancelled, staleThreshold);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Failed to cancel stale pending move jobs");
            }
            return cancelled;
        }
    }
}


