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

namespace Listenarr.Tests.Features.Domain.Downloads
{
    [Trait("Name", "QueueItemMatchScoreTests")]
    [Trait("Category", "DownloadQueue")]
    public class QueueItemMatchScoreTests
    {
        private static Download CreateDownload(string title, string? clientDownloadId = null, string? torrentHash = null)
        {
            var download = new Download
            {
                Id = System.Guid.NewGuid().ToString(),
                Title = title,
                Metadata = new Dictionary<string, object>()
            };
            if (clientDownloadId != null)
            {
                download.Metadata["ClientDownloadId"] = clientDownloadId;
            }

            if (torrentHash != null)
            {
                download.Metadata["TorrentHash"] = torrentHash;
            }

            return download;
        }

        [Fact]
        public void GetMatchScore_MatchingClientId_ScoresHigh()
        {
            var download = CreateDownload("Furies of Calderon", clientDownloadId: "26043");
            var queueItem = new QueueItem { Id = "26043", Title = "Furies of Calderon" };

            Assert.Equal(3, queueItem.GetMatchScore(download));
        }

        [Fact]
        public void GetMatchScore_DownloadWithDifferentClientId_NeverTitleMatches()
        {
            // Live regression: a fresh NZB re-grab (id 26043) was title-matched to a
            // months-old completed history entry (id 25996) for the same release, had
            // its client id overwritten, and imported the stale folder before the
            // real download finished. A download that carries its own client item id
            // is matched by id or not at all.
            var download = CreateDownload("Jim Butcher - Codex Alera, Bk 1 - Furies of Calderon (NMR", clientDownloadId: "26043");
            var staleHistoryItem = new QueueItem
            {
                Id = "25996",
                Title = "Jim Butcher - Codex Alera, Bk 1 - Furies of Calderon (NMR"
            };

            Assert.Equal(0, staleHistoryItem.GetMatchScore(download));
        }

        [Fact]
        public void GetMatchScore_DownloadWithDifferentTorrentHash_NeverTitleMatches()
        {
            var download = CreateDownload("Some Audiobook", torrentHash: "AAAA1111");
            var otherItem = new QueueItem { Id = "BBBB2222", Title = "Some Audiobook" };

            Assert.Equal(0, otherItem.GetMatchScore(download));
        }

        [Fact]
        public void GetMatchScore_IdlessDownload_StillTitleMatches()
        {
            // Legacy rows that never got a client id keep the title fallback.
            var download = CreateDownload("Some Audiobook");
            var queueItem = new QueueItem { Id = "26043", Title = "Some Audiobook" };

            Assert.Equal(2, queueItem.GetMatchScore(download));
        }
    }
}
