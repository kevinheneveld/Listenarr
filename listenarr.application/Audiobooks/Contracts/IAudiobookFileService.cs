
namespace Listenarr.Application.Audiobooks.Contracts
{
    public enum RegistrationPublicationCompletion
    {
        Completed,
        CommittedCleanupPending
    }

    public interface IAudiobookFileRegistrationLease : IDisposable
    {
        string PublicPath { get; }
        string MetadataPath { get; }
        string PhysicalObjectIdentity { get; }
        string? SourcePhysicalObjectIdentity { get; }
        Stream OpenMetadataReadStream() =>
            throw new NotSupportedException(
                "This registration lease does not expose generation-bound metadata reads.");
        Stream OpenMetadataWriteStream() =>
            throw new NotSupportedException(
                "This registration lease does not expose generation-bound metadata writes.");
        bool MatchesCurrentPublication();
        bool PrepareCleanupRecovery(int audiobookId);
        RegistrationPublicationCompletion CompletePublication();
        Task<bool> MatchesContentAsync(
            Stream candidateStream,
            CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Manages audio file metadata extraction and database tracking
    /// </summary>
    public interface IAudiobookFileService
    {
        /// <summary>
        /// Ensure an Audiobook file record exists for the given audiobook and file path. Extract metadata and persist file-level metadata.
        /// </summary>
        /// <param name="audiobook">The audiobook</param>
        /// <param name="filePath">Path to the audio file</param>
        /// <param name="source">Optional source identifier (e.g., "scan", "import")</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>True when a new ownership row was created; false when the file was already owned or could not be claimed.</returns>
        Task<bool> EnsureAudiobookFileAsync(
            Audiobook audiobook,
            string filePath,
            string? source = "scan",
            CancellationToken cancellationToken = default);

        Task<bool> EnsureAudiobookFileAsync(
            Audiobook audiobook,
            IAudiobookFileRegistrationLease registrationLease,
            string? source = "scan",
            CancellationToken cancellationToken = default);

        Task<bool> RefreshPhysicalGenerationAsync(
            Audiobook audiobook,
            int fileId,
            string? expectedPhysicalObjectIdentity,
            IAudiobookFileRegistrationLease registrationLease,
            string? source = "scan",
            CancellationToken cancellationToken = default);

        Task<bool> RollbackPhysicalGenerationClaimAsync(
            Audiobook audiobook,
            int fileId,
            string? expectedPath,
            string expectedPhysicalObjectIdentity,
            CancellationToken cancellationToken = default);

        Task<bool> RegisterPublishedGenerationAsync(
            Audiobook audiobook,
            AudiobookFileOwnershipCheckResult initialOwnership,
            IAudiobookFileRegistrationLease registrationLease,
            string? source = "scan",
            CancellationToken cancellationToken = default);

        Task<bool> RegisterPublishedGenerationWithBasePathAsync(
            Audiobook audiobook,
            AudiobookFileOwnershipCheckResult initialOwnership,
            IAudiobookFileRegistrationLease registrationLease,
            string authoritativeBasePath,
            string? source = "scan",
            CancellationToken cancellationToken = default);

        Task RollbackPublishedGenerationIfStaleAsync(
            Audiobook audiobook,
            IAudiobookFileRegistrationLease registrationLease);

        Task<AudiobookFileOwnershipCheckResult> CheckAudiobookFileOwnershipAsync(
            Audiobook audiobook,
            string plannedPhysicalPath,
            string? plannedBasePath = null,
            CancellationToken cancellationToken = default);

        Task<AudiobookFileClaimResult> ClaimAudiobookFileAsync(
            Audiobook audiobook,
            AudiobookFile file,
            string physicalPath,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Ensure with metadata refresh: when <paramref name="forceMetadataRefresh"/> is true and the
        /// file is already tracked, re-extract its metadata and backfill blank library-level fields
        /// on the audiobook (never overwrites non-blank values).
        /// </summary>
        Task<bool> EnsureAudiobookFileAsync(
            Audiobook audiobook,
            string filePath,
            string? source,
            bool forceMetadataRefresh,
            CancellationToken cancellationToken = default);

        /// <summary>
        /// Delete a single tracked file from an audiobook. Disk-delete failures surface
        /// as warnings; the DB row is still removed. A history entry records the removal.
        /// </summary>
        Task<Files.DeleteAudiobookFileResult> DeleteAudiobookFileAsync(Audiobook audiobook, int fileId, bool deleteFromDisk, string? source = "manual", CancellationToken ct = default);

        /// <summary>
        /// Re-extract metadata for an already-tracked file and backfill blank
        /// library-level fields on the audiobook (never overwrites non-blank values).
        /// </summary>
        Task RefreshTrackedFileMetadataAsync(Audiobook audiobook, string filePath, CancellationToken cancellationToken = default);
    }
}
