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

namespace Listenarr.Domain.Search
{
    /// <summary>
    /// A release the search must never grab again for a specific audiobook.
    /// Written when the user rejects a release's content ("Wrong content —
    /// find a better copy"): without it, re-searching immediately re-grabbed
    /// the exact same release the user just purged — a wrong-content or
    /// collection release whose title matches the book best keeps "winning"
    /// the search forever (two live loops: a 15-book collection re-imported
    /// after every cleanup, and a mislabeled wrong-book release likewise).
    /// </summary>
    public class BlockedRelease
    {
        public int Id { get; set; }
        public int AudiobookId { get; set; }

        /// <summary>The release title as the indexer reported it.</summary>
        public string ReleaseTitle { get; set; } = string.Empty;

        /// <summary>Torrent info-hash when known — the strongest identity.</summary>
        public string? TorrentHash { get; set; }

        public string? Reason { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }
}
