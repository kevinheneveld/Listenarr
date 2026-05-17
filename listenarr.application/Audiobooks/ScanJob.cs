namespace Listenarr.Application.Audiobooks
{
    public class ScanJob
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public int AudiobookId { get; set; }
        public string? Path { get; set; }
        public DateTime EnqueuedAt { get; set; } = DateTime.UtcNow;
        public string Status { get; set; } = "Queued";
        public string? Error { get; set; }

        /// <summary>
        /// When true, the worker re-extracts metadata for already-tracked files
        /// and backfills any blank library-level fields on the parent audiobook.
        /// </summary>
        public bool ForceMetadataRefresh { get; set; }

        /// <summary>
        /// When true, the worker skips the destructive "BasePath missing → delete
        /// tracked AudiobookFile rows" cleanup. Set by library-wide flows (e.g. the
        /// metadata-backfill sweep) where one stale path must not cascade into
        /// data loss across the library.
        /// </summary>
        public bool SkipMissingBasePathCleanup { get; set; }
    }
}
