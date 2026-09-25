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
using Listenarr.Application.Audiobooks.Organizing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.HostedServices.Library
{
    /// <summary>
    /// Drains the "Organize library" batch into the durable move queue, one row
    /// at a time, at whatever pace the filesystem-mutation lock allows. Runs on
    /// the host's stopping token — never on an HTTP request — so a closed tab
    /// or a reverse-proxy timeout can no longer cut a sweep short.
    /// </summary>
    public sealed class OrganizeApplyBackgroundService : BackgroundService
    {
        private const int ProgressLogEvery = 25;

        private readonly IOrganizeApplyBatch _batch;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<OrganizeApplyBackgroundService> _logger;

        public OrganizeApplyBackgroundService(
            IOrganizeApplyBatch batch,
            IServiceScopeFactory scopeFactory,
            ILogger<OrganizeApplyBackgroundService> logger)
        {
            _batch = batch;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("OrganizeApplyBackgroundService started");
            while (!stoppingToken.IsCancellationRequested)
            {
                // Cancel() swaps the channel, so re-resolve the reader after each batch.
                var reader = _batch.Reader;
                try
                {
                    await foreach (var item in reader.ReadAllAsync(stoppingToken))
                    {
                        await ProcessOneAsync(item, stoppingToken);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
            _logger.LogInformation("OrganizeApplyBackgroundService stopped");
        }

        private async Task ProcessOneAsync(OrganizeApplyItem item, CancellationToken stoppingToken)
        {
            var batchToken = _batch.BatchToken;
            if (batchToken.IsCancellationRequested)
            {
                // Race guard: an item read from the old channel after Cancel() ran.
                return;
            }

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, batchToken);
            _batch.MarkStarted(item);
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var enqueuer = scope.ServiceProvider.GetService<IOrganizeMoveEnqueuer>();
                if (enqueuer == null)
                {
                    _batch.MarkFailed(item, "Move enqueue is not available");
                }
                else
                {
                    var outcome = await enqueuer.EnqueueAsync(item, linked.Token);
                    if (outcome.Accepted && outcome.JobId is { } jobId)
                    {
                        _batch.MarkQueued(item, jobId);
                    }
                    else
                    {
                        _logger.LogWarning(
                            "Organize batch: move for audiobook {AudiobookId} was not accepted: {Reason}",
                            item.AudiobookId, outcome.Reason ?? "no reason given");
                        _batch.MarkNotAccepted(item, outcome.Reason ?? "Move not accepted");
                    }
                }
            }
            catch (OperationCanceledException) when (batchToken.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Organize batch: cancelled while queuing audiobook {AudiobookId}", item.AudiobookId);
                _batch.MarkFailed(item, "Batch cancelled");
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Organize batch: failed to queue move for audiobook {AudiobookId}", item.AudiobookId);
                _batch.MarkFailed(item, $"Failed to enqueue: {ex.Message}");
            }

            var snapshot = _batch.Snapshot();
            if (snapshot.Processed % ProgressLogEvery == 0)
            {
                _logger.LogInformation(
                    "Organize batch {BatchId}: {Processed} of {Total} handed to the move queue ({Queued} queued, {NotAccepted} not accepted, {Failed} failed)",
                    snapshot.BatchId, snapshot.Processed, snapshot.Total, snapshot.Queued, snapshot.NotAccepted, snapshot.Failed);
            }
            _batch.MarkBatchFinishedIfDrained();
        }
    }
}
