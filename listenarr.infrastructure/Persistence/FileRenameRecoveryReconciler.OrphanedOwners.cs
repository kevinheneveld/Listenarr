using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Persistence;

// Orphaned-owner handling for the startup reconciler, split out of
// FileRenameRecoveryReconciler.cs to keep it under the architecture size cap.
public sealed partial class FileRenameRecoveryReconciler
{
    /// <summary>
    /// The journal's owner binding (audiobook, or the file under that audiobook) is gone:
    /// the record was deleted, or the file row was transferred to another record after the
    /// mutation. Before this, the reconciler THREW here, which failed the whole startup
    /// phase and disabled every filesystem operation on the instance until an operator
    /// edited the journal row by hand (live: three restarts in September 2026 — a deleted
    /// wrong-content file, then a file transferred between records). Nothing in the
    /// database references the journal's owner any more, so there is no owner metadata
    /// left to reconcile: retire the journal and say so loudly. An interrupted (not yet
    /// completed) mutation is retired the same way rather than parked as NeedsAttention,
    /// because a NeedsAttention journal also fails the instance closed on every restart —
    /// the warning names the paths so the file can be checked on disk.
    /// </summary>
    private async Task RetireOrphanedOwnerBindingAsync(
        FileMutationJournal journal,
        string reason,
        CancellationToken cancellationToken)
    {
        var interrupted = journal.State < FileMutationJournalState.Completed;
        await using var db = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var tracked = await db.FileMutationJournals
            .SingleAsync(candidate => candidate.OperationId == journal.OperationId, cancellationToken);
        tracked.State = FileMutationJournalState.OwnerMetadataReconciled;
        tracked.Error = interrupted
            ? $"Retired at startup: the interrupted mutation's owner binding is gone ({reason}); verify the file on disk between source and destination."
            : $"Retired at startup: the completed mutation's owner binding is gone ({reason}).";
        tracked.UpdatedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogWarning(
            "Retired {Kind} file-mutation journal {OperationId} for audiobook {AudiobookId}: {Reason}; nothing left to reconcile (source {Source}, destination {Destination})",
            interrupted ? "interrupted" : "completed",
            journal.OperationId,
            journal.AudiobookId,
            reason,
            LogRedaction.SanitizeFilePath(journal.SourcePath),
            LogRedaction.SanitizeFilePath(journal.DestinationPath));
    }
}
