/*
 * Listenarr - Audiobook Management System
 * Copyright (C) 2024-2026 Listenarr Contributors
 *
 * This program is free software: you can redistribute it and/or modify
 * it under the terms of the GNU Affero General Public License as published
 * by the Free Software Foundation, either version 3 of the License, or
 * (at your option) any later version.
 */

using Listenarr.Application.Downloads;
using Microsoft.Extensions.DependencyInjection;

namespace Listenarr.Infrastructure.Downloads.Processing
{
    public partial class DownloadProcessingJobProcessor
    {
        /// <summary>
        /// Blocklists the release behind a terminally failed import whose outcome is a
        /// property of the release itself (nothing importable in the payload, or an
        /// import that registered no audio) through the shared
        /// <see cref="FailedReleaseBlocklister"/> (info-hash first, title fallback,
        /// deduplicated). WITHOUT this the book stays wanted, ImportBlocked does not
        /// count as an active download for the automatic-search sweep, and the same
        /// top-scoring release is re-grabbed every cycle to fail the same way (live:
        /// 105 ImportBlocked rows, dozens of books at three or four grabs of the
        /// identical release, 1,992 terminal "No importable files found" imports in
        /// 90 days). Transient failures — source path missing, client unreachable,
        /// file-count mismatch, publication errors — are deliberately NOT blocklisted:
        /// a re-grab could succeed once the environment is fixed. The ImportBlocked
        /// row is untouched, so "retry import" on it keeps working.
        /// </summary>
        private Task BlocklistImportFailedReleaseAsync(
            IServiceScope scope,
            Download download,
            string reason,
            CancellationToken cancellationToken)
            => FailedReleaseBlocklister.BlocklistAsync(
                scope.ServiceProvider.GetService<IBlockedReleaseRepository>(),
                download,
                reason,
                logger,
                cancellationToken);
    }
}
