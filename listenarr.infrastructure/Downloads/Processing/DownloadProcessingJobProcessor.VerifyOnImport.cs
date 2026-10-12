using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Listenarr.Infrastructure.Downloads.Processing
{
    // Verify-on-import hook, split out of DownloadProcessingJobProcessor.cs to keep
    // that file under the architecture size cap.
    public partial class DownloadProcessingJobProcessor
    {
        private async Task TryEnqueueVerifyOnImportAsync(
            IServiceScope scope,
            DownloadProcessingJob job,
            Audiobook audiobook)
        {
            // Auto-verify the freshly imported audio (ADR-0001) when enabled, so a
            // wrong grab gets flagged right away instead of waiting for the next
            // manual library walk. Best-effort: verification must never disrupt the
            // import control flow, and it's skipped entirely when whisper.cpp isn't
            // installed (no point queueing jobs that can only fail).
            try
            {
                var verifyConfigService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
                var importSettings = await verifyConfigService.GetApplicationSettingsAsync();
                if (importSettings?.VerificationOnImport ?? true)
                {
                    var whisper = scope.ServiceProvider.GetRequiredService<Listenarr.Application.Audiobooks.Verification.Contracts.IWhisperService>();
                    if (await whisper.IsAvailableAsync())
                    {
                        var verificationQueue = scope.ServiceProvider.GetRequiredService<Listenarr.Application.Audiobooks.Verification.ILibraryVerificationQueueService>();
                        var verificationJobId = await verificationQueue.EnqueueAsync(
                            new List<int> { audiobook.Id }, Listenarr.Application.Audiobooks.Verification.VerificationTriggers.Import);
                        job.AddLogEntry($"Enqueued verification job {verificationJobId} for audiobook {audiobook.Id}");
                    }
                    else
                    {
                        logger.LogDebug(
                            "Skipping verify-on-import for audiobook {AudiobookId}: whisper.cpp unavailable", audiobook.Id);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                logger.LogWarning(ex, "Failed to enqueue verify-on-import for audiobook {AudiobookId}", audiobook.Id);
            }
        }
    }
}
