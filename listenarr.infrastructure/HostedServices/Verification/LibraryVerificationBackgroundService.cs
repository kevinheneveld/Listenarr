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
using Listenarr.Application.Realtime.Contracts;
using Listenarr.Domain.Audiobooks.Enumerations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.HostedServices.Verification
{
    /// <summary>
    /// Walks queued verification jobs (ADR-0001 Phase 1), running the identity
    /// verifier per book and persisting flag-only verdicts. Channel-fed, SignalR
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
        private readonly IHubBroadcaster _broadcaster;

        public LibraryVerificationBackgroundService(
            ILibraryVerificationQueueService queue,
            IServiceScopeFactory scopeFactory,
            ILogger<LibraryVerificationBackgroundService> logger,
            IHubBroadcaster broadcaster)
        {
            _queue = queue;
            _scopeFactory = scopeFactory;
            _logger = logger;
            _broadcaster = broadcaster;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("LibraryVerificationBackgroundService started");

            // Restart recovery: re-enqueue jobs whose durable rows are still
            // pending — the in-memory channel died with the previous process
            // (live losses: two deploys dropped queued verifications).
            await RehydratePendingJobsAsync(stoppingToken);

            try
            {
                await foreach (var job in _queue.Reader.ReadAllAsync(stoppingToken))
                {
                    // Cancelled while still queued: report and move on without work.
                    if (job.CancelSource.IsCancellationRequested)
                    {
                        job.Status = "Cancelled";
                        job.CompletedAt = DateTime.UtcNow;
                        await MirrorStatusAsync(job, stoppingToken);
                        await SendCompleteAsync(job, error: null, stoppingToken);
                        _logger.LogInformation("Verification job {JobId} cancelled before it started", job.Id);
                        continue;
                    }

                    // Link host shutdown with the job's own cancel so a Stop takes
                    // effect mid-transcription, not only between books.
                    using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, job.CancelSource.Token);
                    try
                    {
                        _logger.LogInformation("Processing verification job {JobId}", job.Id);
                        job.Status = "Processing";
                        await MirrorStatusAsync(job, stoppingToken);
                        await RunJobAsync(job, jobCts.Token);

                        job.Status = "Completed";
                        job.CompletedAt = DateTime.UtcNow;
                        await MirrorStatusAsync(job, stoppingToken);
                        await SendCompleteAsync(job, error: null, stoppingToken);
                        _logger.LogInformation(
                            "Verification job {JobId} completed: {Verified} verified, {Flagged} flagged, {Unverifiable} unverifiable, {Skipped} skipped, {Failed} failed",
                            job.Id, job.Verified, job.Flagged, job.Unverifiable, job.Skipped, job.Failed);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (OperationCanceledException) when (job.CancelSource.IsCancellationRequested)
                    {
                        job.Status = "Cancelled";
                        job.CompletedAt = DateTime.UtcNow;
                        await MirrorStatusAsync(job, stoppingToken);
                        await SendCompleteAsync(job, error: null, stoppingToken);
                        _logger.LogInformation(
                            "Verification job {JobId} cancelled after {Processed}/{Total} books ({Verified} verified, {Flagged} flagged)",
                            job.Id, job.Processed, job.Total, job.Verified, job.Flagged);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
                    {
                        _logger.LogError(ex, "Verification job {JobId} failed", job.Id);
                        job.Status = "Failed";
                        job.Error = ex.Message;
                        job.CompletedAt = DateTime.UtcNow;
                        await MirrorStatusAsync(job, stoppingToken);
                        await SendCompleteAsync(job, ex.Message, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("LibraryVerificationBackgroundService stopping due to host shutdown");
            }
        }

        /// <summary>Best-effort mirror of the in-memory status onto the durable row.</summary>
        private async Task MirrorStatusAsync(VerificationJob job, CancellationToken ct)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var persistence = scope.ServiceProvider.GetService<Listenarr.Application.Audiobooks.Verification.Contracts.IVerificationJobPersistence>();
                if (persistence != null)
                {
                    await persistence.SetStatusAsync(job.Id, job.Status, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Failed to mirror status for verification job {JobId}", job.Id);
            }
        }

        private async Task RehydratePendingJobsAsync(CancellationToken ct)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var persistence = scope.ServiceProvider.GetService<Listenarr.Application.Audiobooks.Verification.Contracts.IVerificationJobPersistence>();
                if (persistence == null) return;

                var pending = await persistence.GetPendingAsync(ct);
                foreach (var record in pending)
                {
                    // Retire the old row first: the re-enqueue below writes a fresh
                    // one, and a crash between the two must not double-enqueue.
                    await persistence.SetStatusAsync(record.Id, "Rehydrated", ct);

                    List<int>? ids = null;
                    if (!string.IsNullOrWhiteSpace(record.AudiobookIdsJson))
                    {
                        try
                        {
                            ids = System.Text.Json.JsonSerializer.Deserialize<List<int>>(record.AudiobookIdsJson);
                        }
                        catch (System.Text.Json.JsonException)
                        {
                            _logger.LogWarning("Verification job {JobId} had unreadable ids; skipping rehydration", record.Id);
                            continue;
                        }
                    }

                    var newId = await _queue.EnqueueAsync(ids, record.Trigger);
                    _logger.LogInformation(
                        "Rehydrated verification job {OldJobId} as {NewJobId} ({Scope}, trigger {Trigger}) after restart",
                        record.Id, newId, ids == null ? "whole library" : $"{ids.Count} book(s)", record.Trigger);
                }

                await persistence.CleanupAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Verification job rehydration failed (queue starts empty)");
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
            // No credit-shaped claims at all: neutral, NOT a flag — absence of
            // credits is not evidence of wrong content.
            VerificationOutcome.NoSpokenCredits => VerificationStatus.AgentUnverifiable,
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
                var audiobook = (await audiobookRepository.GetByIdsWithFilesAsync(new[] { id }, ct)).FirstOrDefault();
                // Surface what the worker is chewing on for the dashboard's
                // background-activity panel.
                job.CurrentAudiobookId = audiobook?.Id;
                job.CurrentAudiobookTitle = audiobook?.Title;
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

                    // Close the loop on confident wrong-content imports: the audio ANNOUNCED
                    // a different book (credit evidence) at high confidence on a fresh import
                    // — purge it, blocklist the release, re-search. Best-effort: a failed
                    // auto-reject leaves the verdict flagged for the human path.
                    await TryAutoRejectWrongContentAsync(scope.ServiceProvider, job, audiobook, verdict, ct);

                    switch (audiobook.VerificationStatus)
                    {
                        case VerificationStatus.AgentVerified: job.Verified++; break;
                        case VerificationStatus.AgentUnverifiable: job.Unverifiable++; break;
                        default: job.Flagged++; break;
                    }

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

            job.CurrentAudiobookId = null;
            job.CurrentAudiobookTitle = null;
        }

        /// <summary>
        /// Auto-reject hook (see <see cref="WrongContentAutoReject"/>): import-triggered
        /// jobs only, requires the settings toggle, a confident Mismatch, and heard-credit
        /// evidence. Runs the same not-audiobook flow as the manual button via
        /// <see cref="IWrongContentAutoRejector"/>. Never throws into the job loop.
        /// </summary>
        private async Task TryAutoRejectWrongContentAsync(
            IServiceProvider services,
            VerificationJob job,
            Audiobook audiobook,
            VerificationVerdict verdict,
            CancellationToken ct)
        {
            try
            {
                var configService = services.GetService<Listenarr.Application.Configuration.Contracts.IConfigurationService>();
                if (configService == null) return;
                var settings = await configService.GetApplicationSettingsAsync();

                if (!WrongContentAutoReject.ShouldAutoReject(
                        settings.VerificationAutoRejectWrongContent,
                        job.Trigger,
                        verdict.Outcome,
                        verdict.Confidence,
                        verdict.HeardCredits?.Title,
                        verdict.HeardCredits?.Author))
                {
                    return;
                }

                var rejector = services.GetService<IWrongContentAutoRejector>();
                if (rejector == null) return;

                _logger.LogInformation(
                    "Auto-rejecting wrong content for audiobook {Id} ('{Title}'): import verification heard \"{HeardTitle}\" by \"{HeardAuthor}\" at confidence {Confidence:0.00}",
                    audiobook.Id, audiobook.Title, verdict.HeardCredits?.Title ?? "?", verdict.HeardCredits?.Author ?? "?", verdict.Confidence);

                await rejector.TryRejectAsync(audiobook.Id, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Auto-reject check failed for audiobook {Id} (verdict remains flagged for manual review)", audiobook.Id);
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
            await _broadcaster.BroadcastAsync(RealtimeHubTarget.Settings, "VerificationProgress", new
            {
                jobId = job.Id.ToString(),
                trigger = job.Trigger,
                processed = job.Processed + 1, // current book counts as done for display
                total = job.Total,
                audiobookId = audiobook.Id,
                title = audiobook.Title,
                outcome
            }, ct);
        }

        private async Task SendCompleteAsync(VerificationJob job, string? error, CancellationToken ct)
        {
            await _broadcaster.BroadcastAsync(RealtimeHubTarget.Settings, "VerificationComplete", new
            {
                jobId = job.Id.ToString(),
                trigger = job.Trigger,
                status = job.Status,
                processed = job.Processed,
                total = job.Total,
                verified = job.Verified,
                flagged = job.Flagged,
                unverifiable = job.Unverifiable,
                skipped = job.Skipped,
                failed = job.Failed,
                error
            }, ct);
        }
    }
}
