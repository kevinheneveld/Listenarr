/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Microsoft.EntityFrameworkCore;

namespace Listenarr.Infrastructure.Persistence.Repositories;

public partial class EfMoveQueuePersistence
{
    public async Task<IReadOnlyList<Guid>> CancelStalePendingAsync(
        DateTimeOffset cutoff,
        string error,
        CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var cutoffUtc = cutoff.UtcDateTime;
        var stale = await db.MoveJobs
            .Where(j => (j.Status == MoveJobStatus.Queued || j.Status == MoveJobStatus.Running)
                        && (j.UpdatedAt ?? j.EnqueuedAt) < cutoffUtc)
            .ToListAsync(cancellationToken);

        foreach (var job in stale)
        {
            // The durable model has no Cancelled state; a sweep-cancelled stale job is a failure with a stated reason.
            job.Status = MoveJobStatus.Failed;
            job.Error = error;
            job.UpdatedAt = DateTime.UtcNow;
            job.ActiveDeduplicationKey = null;
        }

        if (stale.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return stale.Select(j => j.Id).ToList();
    }
}
