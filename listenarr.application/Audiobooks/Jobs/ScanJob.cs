namespace Listenarr.Application.Audiobooks.Jobs
{
    public class ScanJob
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public int AudiobookId { get; set; }
        public string? Path { get; set; }
        public DateTime EnqueuedAt { get; set; } = DateTime.UtcNow;
        public string Status { get; set; } = "Queued";
        public string? Error { get; set; }
        public string? CorrelationId { get; set; }
        public string? DownloadId { get; set; }

        /// <summary>Re-extract metadata for already-tracked files and backfill blank audiobook fields.</summary>
        public bool ForceMetadataRefresh { get; set; }

        /// <summary>
        /// Recovery-scan safety belt: when true, a missing/unreadable BasePath does NOT
        /// cascade into AudiobookFile deletions. Set by the maintenance/recovery flows so
        /// a recovery scan that somehow finds nothing can't wipe tracked files.
        /// </summary>
        public bool SkipMissingBasePathCleanup { get; set; }
    }
}
