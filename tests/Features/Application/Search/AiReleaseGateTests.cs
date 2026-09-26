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

using Listenarr.Application.Search;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Search
{
    /// <summary>
    /// The shared gate used by both the per-book search-and-download path and the automatic
    /// sweep (which, until 2026-09-12, never consulted it and grabbed a political title for a
    /// Patterson novel off a title-only fallback).
    /// </summary>
    [Trait("Name", "AiReleaseGateTests")]
    [Trait("Category", "Search")]
    public class AiReleaseGateTests : BaseTests
    {
        private static readonly Audiobook Book = new() { Id = 1, Title = "American Rage", Authors = new List<string> { "James Patterson" } };

        private static List<QualityScore> Candidates(params string[] titles) =>
            titles.Select((t, i) => new QualityScore
            {
                SearchResult = new SearchResult { Title = t, Size = 100_000_000 },
                TotalScore = 100 - i
            }).ToList();

        private static IConfigurationService Settings(bool gate, bool rank = false)
        {
            var config = new Mock<IConfigurationService>();
            config.Setup(c => c.GetApplicationSettingsAsync()).ReturnsAsync(new ApplicationSettings { AiAssistGateSearches = gate, AiAssistRankSearches = rank });
            return config.Object;
        }

        private static readonly Audiobook NarratedBook = new()
        {
            Id = 2,
            Title = "61 Hours",
            Authors = new List<string> { "Lee Child" },
            Narrators = new List<string> { "Dick Hill" },
            Runtime = 793,
        };

        private static List<QualityScore> Torrents(params (string Title, int Seeders)[] items) =>
            items.Select((t, i) => new QualityScore
            {
                SearchResult = new SearchResult { Title = t.Title, Size = 600L * 1024 * 1024, Seeders = t.Seeders, DownloadType = "torrent" },
                TotalScore = 100 - i
            }).ToList();

        [Fact]
        public async Task PickAsync_RankOn_ValidatedNarratorPreferenceBeatsScoreLeader()
        {
            var ai = new AiAssistServiceMock
            {
                Configured = true,
                Response = "{\"reject\":[],\"ranking\":[{\"index\":1,\"reason\":\"names the target narrator Dick Hill\"},{\"index\":0,\"reason\":\"no narrator named\"}]}"
            };
            var gate = new AiReleaseGate(ai, Settings(true, rank: true), NullLogger.Instance);

            var pick = await gate.PickAsync(NarratedBook, Torrents(("61 Hours - Lee Child [M4B]", 10), ("Lee Child - 61 Hours (Dick Hill)", 10)));

            Assert.Equal("Lee Child - 61 Hours (Dick Hill)", pick!.SearchResult.Title);
        }

        [Fact]
        public async Task PickAsync_RankOn_UnverifiablePreferenceKeepsScoreOrder()
        {
            var ai = new AiAssistServiceMock
            {
                Configured = true,
                Response = "{\"reject\":[],\"ranking\":[{\"index\":1,\"reason\":\"this one just looks better\"},{\"index\":0,\"reason\":\"fine\"}]}"
            };
            var gate = new AiReleaseGate(ai, Settings(true, rank: true), NullLogger.Instance);

            var pick = await gate.PickAsync(NarratedBook, Torrents(("61 Hours - Lee Child [M4B]", 10), ("Lee Child - 61 Hours [MP3]", 10)));

            Assert.Equal("61 Hours - Lee Child [M4B]", pick!.SearchResult.Title);
        }

        [Fact]
        public async Task PickAsync_RankOn_ClaimedFactThatIsFalseKeepsScoreOrder()
        {
            // The model claims more seeders; the numbers say otherwise.
            var ai = new AiAssistServiceMock
            {
                Configured = true,
                Response = "{\"reject\":[],\"ranking\":[{\"index\":1,\"reason\":\"far more seeders\"},{\"index\":0,\"reason\":\"fewer seeders\"}]}"
            };
            var gate = new AiReleaseGate(ai, Settings(true, rank: true), NullLogger.Instance);

            var pick = await gate.PickAsync(NarratedBook, Torrents(("61 Hours - Lee Child [M4B]", 50), ("Lee Child - 61 Hours [MP3]", 4)));

            Assert.Equal("61 Hours - Lee Child [M4B]", pick!.SearchResult.Title);
        }

        [Fact]
        public async Task PickAsync_RankOn_RejectsStillApplyAndRankedRejectIsIgnored()
        {
            var ai = new AiAssistServiceMock
            {
                Configured = true,
                Response = "{\"reject\":[{\"index\":0,\"reason\":\"different book\"}],\"ranking\":[{\"index\":0,\"reason\":\"seeders\"},{\"index\":1,\"reason\":\"the target\"}]}"
            };
            var gate = new AiReleaseGate(ai, Settings(true, rank: true), NullLogger.Instance);

            var pick = await gate.PickAsync(NarratedBook, Torrents(("Sixty-One Minutes - Someone Else", 500), ("Lee Child - 61 Hours [MP3]", 4)));

            Assert.Equal("Lee Child - 61 Hours [MP3]", pick!.SearchResult.Title);
        }

        [Fact]
        public async Task PickAsync_RankOn_GateOff_ReturnsTopPickWithoutAskingTheModel()
        {
            var ai = new AiAssistServiceMock
            {
                Configured = true,
                Response = "{\"reject\":[],\"ranking\":[{\"index\":1,\"reason\":\"names the target narrator Dick Hill\"}]}"
            };
            var gate = new AiReleaseGate(ai, Settings(false, rank: true), NullLogger.Instance);

            var pick = await gate.PickAsync(NarratedBook, Torrents(("61 Hours - Lee Child [M4B]", 10), ("Lee Child - 61 Hours (Dick Hill)", 10)));

            Assert.Equal("61 Hours - Lee Child [M4B]", pick!.SearchResult.Title);
        }

        [Fact]
        public async Task PickAsync_RankOn_GarbageAnswer_ReturnsTopPick()
        {
            var ai = new AiAssistServiceMock { Configured = true, Response = "I would pick the second one." };
            var gate = new AiReleaseGate(ai, Settings(true, rank: true), NullLogger.Instance);

            var pick = await gate.PickAsync(NarratedBook, Torrents(("61 Hours - Lee Child [M4B]", 10), ("Lee Child - 61 Hours (Dick Hill)", 10)));

            Assert.Equal("61 Hours - Lee Child [M4B]", pick!.SearchResult.Title);
        }

        [Fact]
        public async Task PickAsync_GateOff_ReturnsTopPick()
        {
            var gate = new AiReleaseGate(new AiAssistServiceMock { Configured = true, Response = "{\"reject\":[{\"index\":0,\"reason\":\"x\"}]}" }, Settings(false), NullLogger.Instance);

            var pick = await gate.PickAsync(Book, Candidates("White Rural Rage", "American Rage - James Patterson"));

            Assert.Equal("White Rural Rage", pick!.SearchResult.Title);
        }

        [Fact]
        public async Task PickAsync_AiUnconfigured_ReturnsTopPick()
        {
            var gate = new AiReleaseGate(new AiAssistServiceMock { Configured = false }, Settings(true), NullLogger.Instance);

            var pick = await gate.PickAsync(Book, Candidates("White Rural Rage", "American Rage - James Patterson"));

            Assert.Equal("White Rural Rage", pick!.SearchResult.Title);
        }

        [Fact]
        public async Task PickAsync_FlaggedTop_DropsToBestUnflagged()
        {
            var ai = new AiAssistServiceMock { Configured = true, Response = "{\"reject\":[{\"index\":0,\"reason\":\"different book and author\"}]}" };
            var gate = new AiReleaseGate(ai, Settings(true), NullLogger.Instance);

            var pick = await gate.PickAsync(Book, Candidates("White Rural Rage", "American Rage - James Patterson"));

            Assert.Equal("American Rage - James Patterson", pick!.SearchResult.Title);
        }

        [Fact]
        public async Task PickAsync_WholeShortlistFlagged_ReturnsNull()
        {
            var ai = new AiAssistServiceMock { Configured = true, Response = "{\"reject\":[{\"index\":0,\"reason\":\"a\"},{\"index\":1,\"reason\":\"b\"}]}" };
            var gate = new AiReleaseGate(ai, Settings(true), NullLogger.Instance);

            var pick = await gate.PickAsync(Book, Candidates("White Rural Rage", "Rage of Dragons"));

            Assert.Null(pick);
        }

        [Fact]
        public async Task PickAsync_ShortlistFlagged_FallsThroughToUnjudgedCandidate()
        {
            var titles = Enumerable.Range(0, AiReleaseGateJudge.MaxCandidates).Select(i => $"Wrong {i}").Append("Unjudged").ToArray();
            var rejects = string.Join(",", Enumerable.Range(0, AiReleaseGateJudge.MaxCandidates).Select(i => $"{{\"index\":{i},\"reason\":\"no\"}}"));
            var ai = new AiAssistServiceMock { Configured = true, Response = $"{{\"reject\":[{rejects}]}}" };
            var gate = new AiReleaseGate(ai, Settings(true), NullLogger.Instance);

            var pick = await gate.PickAsync(Book, Candidates(titles));

            Assert.Equal("Unjudged", pick!.SearchResult.Title);
        }

        [Fact]
        public async Task PickAsync_GarbageAnswer_ReturnsTopPick()
        {
            var ai = new AiAssistServiceMock { Configured = true, Response = "not json at all" };
            var gate = new AiReleaseGate(ai, Settings(true), NullLogger.Instance);

            var pick = await gate.PickAsync(Book, Candidates("White Rural Rage", "American Rage - James Patterson"));

            Assert.Equal("White Rural Rage", pick!.SearchResult.Title);
        }

        [Fact]
        public async Task PickAsync_NoCandidates_ReturnsNull()
        {
            var gate = new AiReleaseGate(new AiAssistServiceMock { Configured = true }, Settings(true), NullLogger.Instance);

            Assert.Null(await gate.PickAsync(Book, new List<QualityScore>()));
        }
    }
}
