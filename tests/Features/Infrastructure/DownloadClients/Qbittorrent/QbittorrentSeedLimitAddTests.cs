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
using Listenarr.Infrastructure.DownloadClients.Qbittorrent;

namespace Listenarr.Tests.Features.Infrastructure.DownloadClients.Qbittorrent
{
    [Trait("Area", "DownloadClients")]
    [Trait("Name", "QbittorrentSeedLimitAddTests")]
    public class QbittorrentSeedLimitAddTests
    {
        private static DownloadClientConfiguration Client(Dictionary<string, object>? settings = null)
        {
            return new DownloadClientConfiguration
            {
                Id = "qb-1",
                Name = "qBittorrent",
                Type = "qbittorrent",
                DownloadPath = "/downloads/AudioBooks",
                Settings = settings ?? new Dictionary<string, object>()
            };
        }

        private static PreparedTorrentSubmission MagnetSubmission()
        {
            return new PreparedTorrentSubmission(
                "Test Book",
                "Author",
                "Test Book",
                "indexer",
                null,
                null,
                1000,
                "magnet:?xt=urn:btih:abc123",
                "abc123",
                null,
                "magnet:?xt=urn:btih:abc123",
                "book.torrent",
                Array.Empty<string>());
        }

        [Fact]
        public void Planner_ReadsSeedLimitsFromSettings()
        {
            var client = Client(new Dictionary<string, object>
            {
                { "category", "AudioBooks" },
                { "seedRatioLimit", 1.0 },
                { "seedTimeLimitMinutes", 2880 }
            });

            var plan = QbittorrentTorrentAddPlanner.Create(client, MagnetSubmission());

            Assert.Equal(1.0, plan.SeedRatioLimit);
            Assert.Equal(2880, plan.SeedTimeLimitMinutes);
        }

        [Fact]
        public void Planner_AbsentOrGarbageSettings_YieldNoLimits()
        {
            var absent = QbittorrentTorrentAddPlanner.Create(Client(), MagnetSubmission());
            Assert.Null(absent.SeedRatioLimit);
            Assert.Null(absent.SeedTimeLimitMinutes);

            var garbage = QbittorrentTorrentAddPlanner.Create(
                Client(new Dictionary<string, object>
                {
                    { "seedRatioLimit", "not-a-number" },
                    { "seedTimeLimitMinutes", -5 }
                }),
                MagnetSubmission());
            Assert.Null(garbage.SeedRatioLimit);
            Assert.Null(garbage.SeedTimeLimitMinutes);
        }

        [Fact]
        public async Task ContentBuilder_IncludesShareLimitFields_WhenConfigured()
        {
            var plan = new QbittorrentTorrentAddPlan(
                "abc123", "/downloads", "AudioBooks", null, null,
                "magnet:?xt=urn:btih:abc123", null,
                SeedRatioLimit: 1.0, SeedTimeLimitMinutes: 2880);

            var content = QbittorrentAddRequestContentBuilder.Build(plan);
            var body = await content.ReadAsStringAsync();

            Assert.Contains("ratioLimit=1", body);
            Assert.Contains("seedingTimeLimit=2880", body);
        }

        [Fact]
        public async Task ContentBuilder_OmitsShareLimitFields_WhenUnset()
        {
            var plan = new QbittorrentTorrentAddPlan(
                "abc123", "/downloads", "AudioBooks", null, null,
                "magnet:?xt=urn:btih:abc123", null);

            var content = QbittorrentAddRequestContentBuilder.Build(plan);
            var body = await content.ReadAsStringAsync();

            Assert.DoesNotContain("ratioLimit", body);
            Assert.DoesNotContain("seedingTimeLimit", body);
        }

        [Fact]
        public async Task ContentBuilder_IncludesShareLimits_InMultipartTorrentUpload()
        {
            var plan = new QbittorrentTorrentAddPlan(
                "abc123", "/downloads", "AudioBooks", null,
                new byte[] { 1, 2, 3 }, null, "book.torrent",
                SeedRatioLimit: 0.5, SeedTimeLimitMinutes: 60);

            var content = QbittorrentAddRequestContentBuilder.Build(plan);
            var body = await content.ReadAsStringAsync();

            Assert.Contains("name=ratioLimit", body);
            Assert.Contains("0.5", body);
            Assert.Contains("name=seedingTimeLimit", body);
            Assert.Contains("60", body);
        }
    }
}
