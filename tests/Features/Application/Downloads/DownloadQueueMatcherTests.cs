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
using Listenarr.Application.Downloads;
using Listenarr.Domain.Models;
using Xunit;

namespace Listenarr.Tests.Features.Application.Downloads
{
    /// <summary>
    /// The index-based fast path must select the exact same <see cref="Download"/>
    /// as the original full scan for every queue item — everything downstream
    /// (enrichment, known-id suppression, orphan retention) is derived from that
    /// selection, so identical selection means identical reconcile behavior.
    /// </summary>
    public class DownloadQueueMatcherTests
    {
        private static Download Dl(
            string client,
            string id,
            string title = "",
            string artist = "",
            DateTime? startedAt = null,
            string? clientDownloadId = null,
            string? torrentHash = null)
        {
            var d = new Download
            {
                Id = id,
                DownloadClientId = client,
                Title = title,
                Artist = artist,
                StartedAt = startedAt ?? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            };
            if (clientDownloadId != null) d.Metadata["ClientDownloadId"] = clientDownloadId;
            if (torrentHash != null) d.Metadata["TorrentHash"] = torrentHash;
            return d;
        }

        private static QueueItem Qi(string id, string title = "") => new() { Id = id, Title = title };

        private static Download? Reference(QueueItem qi, string client, IEnumerable<Download> all)
            => DownloadQueueMatcher.FindBestMatch(qi, all.Where(d => d.DownloadClientId == client).ToList());

        private static Download? Fast(QueueItem qi, string client, DownloadQueueMatcher.Index index)
            => DownloadQueueMatcher.FindBestMatch(qi, index.ForClient(client));

        [Fact]
        public void IdMatch_WinsAndIsReturned()
        {
            var all = new List<Download> { Dl("qb", "abc"), Dl("qb", "def", torrentHash: "abc") };
            var index = DownloadQueueMatcher.BuildIndex(all);
            var qi = Qi("abc");

            // Reference scores the Id-match (4) over the TorrentHash-match (3).
            Assert.Equal("abc", Reference(qi, "qb", all)!.Id);
            Assert.Equal(Reference(qi, "qb", all)!.Id, Fast(qi, "qb", index)!.Id);
        }

        [Fact]
        public void ClientDownloadIdMatch_IsReturned()
        {
            var all = new List<Download> { Dl("qb", "internal-1", clientDownloadId: "client-xyz") };
            var index = DownloadQueueMatcher.BuildIndex(all);
            var qi = Qi("client-xyz");

            Assert.Equal("internal-1", Fast(qi, "qb", index)!.Id);
            Assert.Equal(Reference(qi, "qb", all)!.Id, Fast(qi, "qb", index)!.Id);
        }

        [Fact]
        public void TorrentHashMatch_IsReturned()
        {
            var all = new List<Download> { Dl("qb", "internal-2", torrentHash: "HASHHASH") };
            var index = DownloadQueueMatcher.BuildIndex(all);
            var qi = Qi("hashhash"); // case-insensitive

            Assert.Equal("internal-2", Fast(qi, "qb", index)!.Id);
            Assert.Equal(Reference(qi, "qb", all)!.Id, Fast(qi, "qb", index)!.Id);
        }

        [Fact]
        public void Score3Tiebreak_PrefersLatestStartedAt_AcrossBothMetadataSources()
        {
            // Two downloads collide on the same identity value "k" — one via
            // ClientDownloadId (older), one via TorrentHash (newer). The old scan
            // orders score-3 matches by StartedAt desc, so the newer one wins.
            var older = Dl("qb", "old", clientDownloadId: "k", startedAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var newer = Dl("qb", "new", torrentHash: "k", startedAt: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
            var all = new List<Download> { older, newer };
            var index = DownloadQueueMatcher.BuildIndex(all);
            var qi = Qi("k");

            Assert.Equal("new", Reference(qi, "qb", all)!.Id);
            Assert.Equal(Reference(qi, "qb", all)!.Id, Fast(qi, "qb", index)!.Id);
        }

        [Fact]
        public void Score4_BeatsScore3()
        {
            var idMatch = Dl("qb", "k", startedAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            var hashMatch = Dl("qb", "other", torrentHash: "k", startedAt: new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc));
            var all = new List<Download> { hashMatch, idMatch };
            var index = DownloadQueueMatcher.BuildIndex(all);
            var qi = Qi("k");

            // Even though the hash-match is newer, the Id-match scores higher.
            Assert.Equal("k", Reference(qi, "qb", all)!.Id);
            Assert.Equal(Reference(qi, "qb", all)!.Id, Fast(qi, "qb", index)!.Id);
        }

        [Fact]
        public void IdentityMatch_BeatsTitleMatch()
        {
            var hashMatch = Dl("qb", "h", torrentHash: "k");
            var titleMatch = Dl("qb", "t", title: "The Great Book");
            var all = new List<Download> { titleMatch, hashMatch };
            var index = DownloadQueueMatcher.BuildIndex(all);
            var qi = Qi("k", "The Great Book");

            Assert.Equal("h", Reference(qi, "qb", all)!.Id);
            Assert.Equal(Reference(qi, "qb", all)!.Id, Fast(qi, "qb", index)!.Id);
        }

        [Fact]
        public void NoIdentityMatch_FallsBackToTitleScan_Equivalently()
        {
            var titleMatch = Dl("qb", "t", title: "The Great Book");
            var all = new List<Download> { titleMatch };
            var index = DownloadQueueMatcher.BuildIndex(all);
            var qi = Qi("unknown-id", "The Great Book");

            Assert.Equal("t", Fast(qi, "qb", index)!.Id);
            Assert.Equal(Reference(qi, "qb", all)!.Id, Fast(qi, "qb", index)!.Id);
        }

        [Fact]
        public void NoMatch_ReturnsNull()
        {
            var all = new List<Download> { Dl("qb", "x", title: "Totally Different") };
            var index = DownloadQueueMatcher.BuildIndex(all);
            var qi = Qi("nope", "Something Else Entirely");

            Assert.Null(Fast(qi, "qb", index));
            Assert.Null(Reference(qi, "qb", all));
        }

        [Fact]
        public void CrossClient_DoesNotMatchOtherClientsDownload()
        {
            var all = new List<Download> { Dl("nzbget", "k", torrentHash: "k") };
            var index = DownloadQueueMatcher.BuildIndex(all);
            var qi = Qi("k");

            // qb has no candidates at all — ForClient returns null → no match.
            Assert.Null(index.ForClient("qb"));
            Assert.Null(Fast(qi, "qb", index));
            Assert.Equal(Reference(qi, "qb", all), Fast(qi, "qb", index));
        }

        [Fact]
        public void Differential_FastPathMatchesReference_AcrossCorpus()
        {
            var clients = new[] { "qb", "nzbget" };
            var all = new List<Download>();
            foreach (var client in clients)
            {
                for (var i = 0; i < 40; i++)
                {
                    all.Add(Dl(
                        client,
                        $"{client}-id-{i}",
                        title: i % 3 == 0 ? $"Shared Title {i % 7}" : $"Unique Title {client} {i}",
                        artist: $"Author {i % 5}",
                        startedAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddHours(i),
                        clientDownloadId: i % 2 == 0 ? $"cdid-{client}-{i}" : null,
                        torrentHash: i % 4 == 0 ? $"hash-{client}-{i % 9}" : null)); // some hash collisions
                }
            }

            var index = DownloadQueueMatcher.BuildIndex(all);

            // Probe with queue items hitting every tier: direct ids, client ids,
            // hashes (incl. colliding ones), shared titles, and misses.
            var probes = new List<string>();
            probes.AddRange(all.Select(d => d.Id));
            probes.AddRange(all.Select(d => d.GetMetadataString("ClientDownloadId")).Where(s => s != null)!);
            probes.AddRange(all.Select(d => d.GetMetadataString("TorrentHash")).Where(s => s != null)!);
            probes.AddRange(new[] { "missing-1", "missing-2", "Shared Title 3", "hash-qb-0" });

            foreach (var client in clients)
            {
                foreach (var probeId in probes)
                {
                    foreach (var title in new[] { "", "Shared Title 3", "Unique Title qb 12" })
                    {
                        var qi = Qi(probeId!, title);
                        var expected = Reference(qi, client, all);
                        var actual = Fast(qi, client, index);
                        Assert.True(
                            ReferenceEquals(expected, actual),
                            $"Mismatch for client={client} id={probeId} title='{title}': expected {expected?.Id ?? "null"}, got {actual?.Id ?? "null"}");
                    }
                }
            }
        }
    }
}
