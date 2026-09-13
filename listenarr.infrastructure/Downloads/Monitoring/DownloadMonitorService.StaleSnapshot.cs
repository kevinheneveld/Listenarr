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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Downloads.Monitoring
{
    // Stale-snapshot guard for the poll loop, split out of DownloadMonitorService.cs to
    // keep it under the architecture size cap.
    public partial class DownloadMonitorProcessor
    {
        /// <summary>
        /// The poll works on a snapshot of the active downloads loaded at cycle start and
        /// saves each entity whole once the client has answered — which can take a while
        /// (hundreds of items, a slow client). If another writer moved the row on in the
        /// meantime, saving the snapshot silently reverts that change. Live case: import
        /// finalization set 14 downloads to Moved (history "Download import committed"
        /// recorded) and every one was reverted to ImportPending by the next monitor save,
        /// so the Activity page showed "Importing" for weeks on books already in the library.
        /// Re-read the row and skip this cycle's save when its status no longer matches the
        /// snapshot; the next cycle starts from the fresh state.
        /// </summary>
        private async Task<bool> WasChangedByAnotherWriterAsync(Download polled, Download? previous)
        {
            if (previous == null)
            {
                return false;
            }

            using var checkScope = scopeFactory.CreateScope();
            var repository = checkScope.ServiceProvider.GetRequiredService<IDownloadRepository>();
            var fresh = await repository.GetByIdAsync(polled.Id);
            if (fresh == null)
            {
                logger.LogDebug("Skipping monitor update for download {Id}: row no longer exists", LogRedaction.SanitizeText(polled.Id));
                return true;
            }

            if (!StatusChangedByAnotherWriter(previous.Status, fresh.Status))
            {
                return false;
            }

            logger.LogInformation(
                "Skipping monitor update for download {Id}: status changed from {Snapshot} to {Fresh} by another writer during the poll",
                LogRedaction.SanitizeText(polled.Id), previous.Status, fresh.Status);
            return true;
        }

        internal static bool StatusChangedByAnotherWriter(DownloadStatus snapshot, DownloadStatus fresh) => snapshot != fresh;
    }
}
