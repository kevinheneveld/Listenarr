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
using System.Text.Json;
using System.Text.Json.Serialization;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Application.Notification;
using Listenarr.Domain.Models;
using Listenarr.Domain.Models.Enumerations;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Audiobooks.Verification
{
    /// <summary>
    /// Walks queued verification jobs (ADR-0001 Phase 1), running the identity
    /// verifier per book and persisting flag-only verdicts. Modeled on
    /// <see cref="UnmatchedScanBackgroundService"/>: channel-fed, SignalR
    /// progress on the settings hub. Books are processed serially — whisper.cpp
    /// saturates CPU on its own, so per-book concurrency would only thrash.
    /// </summary>
    public class LibraryVerificationBackgroundService : BackgroundService
    {
        // Verdict detail is persisted as camelCase JSON with string enums so the
        // FE can consume it without a second mapping layer.
        private static readonly JsonSerializerOptions DetailJsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Converters = { new JsonStringEnumConverter() }
        };

        private readonly ILibraryVerificationQueueService _queue;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<LibraryVerificationBackgroundService> _logger;
        private readonly IHubContext<SettingsHub> _hubContext;

        public LibraryVerificationBackgroundService(
            ILibraryVerificationQueueService queue,
            IServiceScopeFactory scopeFactory,
            ILogger<LibraryVerificationBackgroundService> logger,
            IHubContext<SettingsHub> hubContext)
        {
            _queue = queue;
            _scopeFactory = scopeFactory;
            _logger = logger;
            _hubContext = hubContext;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("LibraryVerificationBackgroundService started");
            try
            {
                await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
                {
                    try
                    {
                        _logger.LogInformation("Processing verification job {JobId}", job.Id);
                        job.Status = "Processing";
                        await RunJobAsync(job, stoppingToken);

                        job.Status = "Completed";
                        job.CompletedAt = DateTime.UtcNow;
                        await SendCompleteAsync(job, error: null, stoppingToken);
                        _logger.LogInformation(
                            "Verification job {JobId} completed: {Verified} verified, {Flagged} flagged, {Skipped} skipped, {Failed} failed",
                            job.Id, job.Verified, job.Flagged, job.Skipped, job.Failed);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogError(ex, "Verification job {JobId} failed", job.Id);
                        job.Status = "Failed";
                        job.Error = ex.Message;
                        job.CompletedAt = DateTime.UtcNow;
                        await SendCompleteAsync(job, ex.Message, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("LibraryVerificationBackgroundService stopping due to host shutdown");
            }
        }

        /// <summary>
        /// Whether an agent pass may process this book. Manual states are sticky
        /// in every mode; a library walk additionally skips books an agent has
        /// already judged so re-running the batch is idempotent. Public + pure
        /// so the rule is directly unit-testable.
        /// </summary>
        public static bool ShouldVerify(Audiobook audiobook, bool explicitRequest)
        {
            if (!audiobook.VerificationStatus.IsAgentWritable()) return false;
            if (!explicitRequest && audiobook.VerificationStatus != VerificationStatus.Unverified) return false;
            return true;
        }

        /// <summary>Maps the verifier's aggregate outcome onto the persisted status.</summary>
        public static VerificationStatus StatusFor(VerificationOutcome outcome) => outcome switch
        {
            VerificationOutcome.Match => VerificationStatus.AgentVerified,
            // Mismatch AND Uncertain both need a human eye; the persisted detail
            // JSON distinguishes them in the triage UI.
            _ => VerificationStatus.AgentFlagged
        };

        private async Task RunJobAsync(VerificationJob job, CancellationToken ct)
        {
            using var scope = _scopeFactory.CreateScope();
            var audiobookRepository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
            var verifier = scope.ServiceProvider.GetRequiredService<IIdentityVerifier>();
            var whisper = scope.ServiceProvider.GetRequiredService<IWhisperService>();

            if (!await whisper.IsAvailableAsync())
            {
                throw new InvalidOperationException(
                    "whisper.cpp binary or model not found — verification requires the bundled STT engine. " +
                    "Run the Docker image (which bakes it in) or point LISTENARR_WHISPER_BIN / LISTENARR_WHISPER_MODEL at a local install.");
            }

            var explicitRequest = job.AudiobookIds != null;
            var candidateIds = await ResolveCandidateIdsAsync(job, audiobookRepository);
            job.Total = candidateIds.Count;

            foreach (var id in candidateIds)
            {
                ct.ThrowIfCancellationRequested();

                // Re-load with files per book: a whole-library walk can run for
                // hours and stale tracked entities would clobber concurrent edits.
                var audiobook = (await audiobookRepository.GetByIdsWithFilesAsync(new[] { id })).FirstOrDefault();
                if (audiobook == null || !ShouldVerify(audiobook, explicitRequest))
                {
                    job.Processed++;
                    job.Skipped++;
                    continue;
                }

                var (firstFile, _) = VerificationFileSelection.SelectFirstAndLast(audiobook);
                if (firstFile == null || !File.Exists(firstFile))
                {
                    job.Processed++;
                    job.Skipped++;
                    _logger.LogDebug("Verification skipping audiobook {Id}: no audio file on disk", audiobook.Id);
                    continue;
                }

                try
                {
                    var verdict = await verifier.VerifyAsync(audiobook, firstFile, ct);
                    ApplyVerdict(audiobook, verdict, whisper.ModelName);
                    await audiobookRepository.UpdateAsync(audiobook);

                    if (audiobook.VerificationStatus == VerificationStatus.AgentVerified) job.Verified++;
                    else job.Flagged++;

                    await SendProgressAsync(job, audiobook, verdict.Outcome.ToString(), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
                {
                    // One unreadable/corrupt book must not abort a library walk.
                    job.Failed++;
                    _logger.LogWarning(ex, "Verification failed for audiobook {Id}", audiobook.Id);
                }
                finally
                {
                    job.Processed++;
                }
            }
        }

        /// <summary>Stamp the verdict onto the entity (status + audit fields + detail JSON).</summary>
        public static void ApplyVerdict(Audiobook audiobook, VerificationVerdict verdict, string modelName)
        {
            audiobook.VerificationStatus = StatusFor(verdict.Outcome);
            audiobook.VerificationConfidence = verdict.Confidence;
            audiobook.VerifiedAt = DateTime.UtcNow;
            audiobook.VerifiedBy = $"agent:whisper-{modelName}";
            audiobook.VerificationMethod = verdict.Method;
            audiobook.VerificationTranscript = verdict.Transcript;
            // Transcript lives in its own column; don't store it twice.
            audiobook.VerificationDetailJson = JsonSerializer.Serialize(verdict with { Transcript = null }, DetailJsonOptions);
        }

        private static async Task<List<int>> ResolveCandidateIdsAsync(VerificationJob job, IAudiobookRepository repository)
        {
            if (job.AudiobookIds != null) return job.AudiobookIds.Distinct().ToList();

            var all = await repository.GetAllAsync();
            return all
                .Where(a => ShouldVerify(a, explicitRequest: false))
                .Select(a => a.Id)
                .ToList();
        }

        private async Task SendProgressAsync(VerificationJob job, Audiobook audiobook, string outcome, CancellationToken ct)
        {
            await _hubContext.Clients.All.SendAsync("VerificationProgress", new
            {
                jobId = job.Id.ToString(),
                processed = job.Processed + 1, // current book counts as done for display
                total = job.Total,
                audiobookId = audiobook.Id,
                title = audiobook.Title,
                outcome
            }, ct);
        }

        private async Task SendCompleteAsync(VerificationJob job, string? error, CancellationToken ct)
        {
            await _hubContext.Clients.All.SendAsync("VerificationComplete", new
            {
                jobId = job.Id.ToString(),
                processed = job.Processed,
                total = job.Total,
                verified = job.Verified,
                flagged = job.Flagged,
                skipped = job.Skipped,
                failed = job.Failed,
                error
            }, ct);
        }
    }
}
