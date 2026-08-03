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
using Listenarr.Domain.Downloads;
using Listenarr.Infrastructure.DownloadClients.Nzbget;

namespace Listenarr.Tests.Features.Infrastructure.DownloadClients.Nzbget
{
    [Trait("Area", "Infrastructure")]
    [Trait("Name", "NzbgetDownloadMatchingTests")]
    public class NzbgetDownloadMatchingTests
    {
        private static Download MakeDownload(string title, string? clientId)
        {
            var download = new Download
            {
                Id = Guid.NewGuid().ToString(),
                Title = title,
                Status = DownloadStatus.Downloading
            };
            if (clientId != null)
            {
                download.Metadata[Download.METADATA_EXTERNAL_ID_KEY] = clientId;
            }
            return download;
        }

        private static NzbgetHistoryEntry MakeEntry(string id, string title) => new()
        {
            CanonicalNzbId = id,
            Title = title,
            Category = "audiobooks",
            RawStatus = "SUCCESS/PAR",
            Outcome = NzbgetHistoryOutcome.Completed,
            DestDir = "/downloads/" + title,
            FinalDir = string.Empty,
            TotalSizeBytes = 1,
            DownloadedSizeBytes = 1,
            HistoryTimeUtc = null
        };

        [Fact]
        public void HistoryEntry_NeverTitleMatchesASameNamedSiblingWithADifferentClientId()
        {
            // Live case: seven simultaneous grabs of "Homer - The Odyssey (64kb)"
            // for different library records. The first completed entry must not
            // claim a still-downloading sibling just because the titles match.
            var sibling = MakeDownload("Homer - The Odyssey (64kb)", "25652");
            var tracked = new List<Download> { sibling };
            var entry = MakeEntry("25645", "Homer - The Odyssey (64kb)");

            var match = NzbgetDownloadPollingWorkflow.FindHistoryDownload(
                entry,
                new Dictionary<string, Download>(StringComparer.OrdinalIgnoreCase),
                tracked,
                new HashSet<Download>());

            Assert.Null(match);
        }

        [Fact]
        public void HistoryEntry_MatchesById()
        {
            var download = MakeDownload("Homer - The Odyssey (64kb)", "25645");
            var byId = new Dictionary<string, Download>(StringComparer.OrdinalIgnoreCase)
            {
                ["25645"] = download
            };

            var match = NzbgetDownloadPollingWorkflow.FindHistoryDownload(
                MakeEntry("25645", "Homer - The Odyssey (64kb)"),
                byId,
                new List<Download> { download },
                new HashSet<Download>());

            Assert.Same(download, match);
        }

        [Fact]
        public void HistoryEntry_StillTitleMatchesALegacyRowWithoutAClientId()
        {
            // Rows created before the external id was recorded have only the
            // title as identity — the fallback must keep working for them.
            var legacy = MakeDownload("Some Old Book", clientId: null);

            var match = NzbgetDownloadPollingWorkflow.FindHistoryDownload(
                MakeEntry("999", "Some Old Book"),
                new Dictionary<string, Download>(StringComparer.OrdinalIgnoreCase),
                new List<Download> { legacy },
                new HashSet<Download>());

            Assert.Same(legacy, match);
        }
    }
}
