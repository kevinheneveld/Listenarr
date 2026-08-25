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

namespace Listenarr.Infrastructure.Downloads.Cleanup
{
    public partial class MovedDownloadCleanupProcessor
    {
        private static async Task<ImportProof> ResolveImportProofAsync(
            Download download,
            IDownloadProcessingJobRepository processingJobRepository,
            IHistoryRepository historyRepository,
            IDownloadHistoryRepository downloadHistoryRepository,
            CancellationToken cancellationToken)
        {
            var completedJob = (await processingJobRepository.GetByDownloadIdAsync(download.Id))
                .Where(job => job.Status == ProcessingJobStatus.Completed)
                .OrderByDescending(job => job.CompletedAt ?? job.CreatedAt)
                .FirstOrDefault();
            if (completedJob != null)
            {
                return new ImportProof(
                    ImportProofKind.CompletedProcessingJob,
                    completedJob.GetOrCreateCorrelationId(),
                    completedJob.Id,
                    completedJob.CompletedAt,
                    completedJob.JobData.TryGetValue(
                        "SourceRetained",
                        out var retainedValue)
                    && bool.TryParse(
                        retainedValue?.ToString(),
                        out var sourceRetained)
                    && sourceRetained);
            }

            if (download.LastImportedAt.HasValue)
            {
                return new ImportProof(
                    ImportProofKind.LastImportedAt,
                    download.Id.ToUpperInvariant(),
                    null,
                    download.LastImportedAt.Value);
            }

            var importedHistory = await historyRepository.GetSucceededImportedByDownloadIdAsync(
                download.Id,
                cancellationToken);
            if (importedHistory != null)
            {
                return new ImportProof(
                    ImportProofKind.ImportedHistory,
                    importedHistory.CorrelationId ?? download.Id.ToUpperInvariant(),
                    null,
                    importedHistory.Timestamp);
            }

            var legacyDownloadHistory = await downloadHistoryRepository.GetImportedByDownloadIdAsync(
                download.Id,
                cancellationToken);
            if (legacyDownloadHistory != null)
            {
                return new ImportProof(
                    ImportProofKind.LegacyDownloadHistory,
                    download.Id.ToUpperInvariant(),
                    null,
                    legacyDownloadHistory.ImportedAt ?? legacyDownloadHistory.EventDate);
            }

            var oldestHistoryAt = await historyRepository.GetOldestTimestampByDownloadIdAsync(
                download.Id,
                cancellationToken);
            if (oldestHistoryAt.HasValue && DateTime.UtcNow - oldestHistoryAt.Value > LegacyMovedProofGracePeriod)
            {
                // Older builds used Moved as the only durable import marker. This is enough
                // to clean stale client/DB state, but not enough to delete external files.
                return new ImportProof(
                    ImportProofKind.LegacyMovedState,
                    download.Id.ToUpperInvariant(),
                    null,
                    oldestHistoryAt.Value);
            }

            return new ImportProof(
                ImportProofKind.None,
                download.Id.ToUpperInvariant(),
                null,
                null);
        }

        private static Dictionary<string, object> BuildCleanupDetails(
            ImportProof proof,
            string removalPolicy,
            bool deleteFiles)
        {
            var details = new Dictionary<string, object>
            {
                ["ImportProof"] = proof.Kind.ToString(),
                ["RemovalPolicy"] = removalPolicy,
                ["DeleteFiles"] = deleteFiles,
                ["SourceRetained"] = proof.SourceRetained
            };

            if (!string.IsNullOrWhiteSpace(proof.ProcessingJobId))
            {
                details["ProcessingJobId"] = proof.ProcessingJobId;
            }

            if (proof.ProvenAt.HasValue)
            {
                details["ImportProofAt"] = proof.ProvenAt.Value;
            }

            return details;
        }

        private static Task AddCleanupHistoryAsync(
            IHistoryRepository historyRepository,
            Download download,
            string eventType,
            HistoryOutcome outcome,
            string correlationId,
            string message,
            Dictionary<string, object> details,
            CancellationToken ct) =>
            historyRepository.AddAsync(new History
            {
                AudiobookId = download.AudiobookId,
                AudiobookTitle = download.Title,
                SourceTitle = download.Title,
                DownloadId = download.Id.ToUpperInvariant(),
                DownloadClientId = download.DownloadClientId,
                EventType = eventType,
                Outcome = outcome,
                Source = "DownloadCleanup",
                Message = message,
                Error = outcome == HistoryOutcome.Failed ? message : null,
                Timestamp = DateTime.UtcNow,
                CorrelationId = correlationId,
                Data = JsonSerializer.Serialize(details)
            }, ct);
    }
}
