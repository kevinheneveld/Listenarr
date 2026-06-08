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
using Listenarr.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Downloads
{
    /// <summary>
    /// Matches a live download-client queue item to the tracked Listenarr
    /// <see cref="Download"/> it represents.
    ///
    /// <para>The previous implementation scored <em>every</em> tracked download for
    /// <em>every</em> queue item (O(queueItems × candidates)). With a large library
    /// (hundreds of live torrents × thousands of tracked downloads) the dominant
    /// cost was <see cref="QueueItem.GetMatchScore"/> falling through to
    /// <c>TitleUtils.NormalizeTitle</c> (seven regex passes) for each of the ~N×M
    /// non-matching pairs — seconds of CPU on every queue-snapshot cycle.</para>
    ///
    /// <para><see cref="BuildIndex"/> indexes candidates once per snapshot by their
    /// identity keys, so the common case — a queue item whose client id / torrent
    /// hash matches a tracked download — resolves with an O(1) dictionary lookup
    /// and never normalizes a title. The title scan (<see cref="FindBestMatch(QueueItem, IReadOnlyList{Download}, ILogger?)"/>)
    /// is run only as a fallback when no identity match exists. This is provably
    /// equivalent to the old full scan: an identity match scores ≥ 3 and a title
    /// match ≤ 2, so an identity hit is always the maximum-scoring match.</para>
    /// </summary>
    internal static class DownloadQueueMatcher
    {
        /// <summary>Per-snapshot index of tracked downloads, grouped by client.</summary>
        internal sealed class Index
        {
            private readonly Dictionary<string, ClientIndex> _byClient;

            internal Index(Dictionary<string, ClientIndex> byClient) => _byClient = byClient;

            public ClientIndex? ForClient(string? clientId)
                => !string.IsNullOrWhiteSpace(clientId) && _byClient.TryGetValue(clientId, out var ci) ? ci : null;
        }

        internal sealed class ClientIndex
        {
            /// <summary>Download.Id → download (score 4: queue item id equals a tracked download id).</summary>
            public Dictionary<string, Download> ById { get; } = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>
            /// ClientDownloadId / TorrentHash → download (score 3). Both metadata
            /// sources share one dictionary so that, when several candidates collide
            /// on a key (re-grabs), the latest <see cref="Download.StartedAt"/> wins —
            /// matching the old scan's <c>OrderByDescending(StartedAt)</c> tiebreak
            /// across all score-3 matches.
            /// </summary>
            public Dictionary<string, Download> ByIdentityKey { get; } = new(StringComparer.OrdinalIgnoreCase);

            /// <summary>This client's candidates, for the title-scan fallback.</summary>
            public List<Download> Candidates { get; } = new();
        }

        public static Index BuildIndex(IEnumerable<Download>? candidates)
        {
            var byClient = new Dictionary<string, ClientIndex>(StringComparer.OrdinalIgnoreCase);
            if (candidates == null) return new Index(byClient);

            foreach (var download in candidates)
            {
                if (download == null || string.IsNullOrWhiteSpace(download.DownloadClientId)) continue;

                if (!byClient.TryGetValue(download.DownloadClientId, out var clientIndex))
                {
                    clientIndex = new ClientIndex();
                    byClient[download.DownloadClientId] = clientIndex;
                }

                clientIndex.Candidates.Add(download);

                if (!string.IsNullOrWhiteSpace(download.Id))
                {
                    UpsertLatest(clientIndex.ById, download.Id, download);
                }

                foreach (var key in EnumerateIdentityKeys(download))
                {
                    UpsertLatest(clientIndex.ByIdentityKey, key, download);
                }
            }

            return new Index(byClient);
        }

        /// <summary>
        /// Fast path: resolve a queue item to its tracked download via the identity
        /// index, falling back to the title scan only when no identity match exists.
        /// </summary>
        public static Download? FindBestMatch(QueueItem? queueItem, ClientIndex? clientIndex, ILogger? logger = null)
        {
            if (queueItem == null || clientIndex == null) return null;

            if (!string.IsNullOrWhiteSpace(queueItem.Id))
            {
                if (clientIndex.ById.TryGetValue(queueItem.Id, out var idMatch)) return idMatch;          // score 4
                if (clientIndex.ByIdentityKey.TryGetValue(queueItem.Id, out var keyMatch)) return keyMatch; // score 3
            }

            return FindBestMatch(queueItem, clientIndex.Candidates, logger);
        }

        /// <summary>
        /// Reference scan over a single client's candidates — the original
        /// <c>GetMatchScore</c>-based selection (highest score, then latest
        /// <see cref="Download.StartedAt"/>, with the ambiguous score-1 title-only
        /// case left unmatched). Kept as the authoritative title-matching path and
        /// reused as the differential-test oracle.
        /// </summary>
        public static Download? FindBestMatch(QueueItem? queueItem, IReadOnlyList<Download>? clientCandidates, ILogger? logger = null)
        {
            if (queueItem == null || clientCandidates == null || clientCandidates.Count == 0) return null;

            var matches = clientCandidates
                .Select(download => new { Download = download, Score = queueItem.GetMatchScore(download) })
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => x.Download.StartedAt)
                .ToList();

            if (matches.Count == 0) return null;

            var bestMatch = matches[0];
            if (bestMatch.Score == 1 && matches.Skip(1).Any(x => x.Score == bestMatch.Score))
            {
                logger?.LogDebug(
                    "Queue item {QueueId} '{QueueTitle}' had ambiguous title-only matches; leaving unmatched",
                    queueItem.Id,
                    queueItem.Title);
                return null;
            }

            return bestMatch.Download;
        }

        private static IEnumerable<string> EnumerateIdentityKeys(Download download)
        {
            var clientDownloadId = download.GetMetadataString("ClientDownloadId");
            if (!string.IsNullOrWhiteSpace(clientDownloadId)) yield return clientDownloadId;

            var torrentHash = download.GetMetadataString("TorrentHash");
            if (!string.IsNullOrWhiteSpace(torrentHash)) yield return torrentHash;
        }

        private static void UpsertLatest(Dictionary<string, Download> dict, string key, Download download)
        {
            if (dict.TryGetValue(key, out var existing) && existing.StartedAt >= download.StartedAt) return;
            dict[key] = download;
        }
    }
}
