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

namespace Listenarr.Tests.Features.Application.Search
{
    [Trait("Name", "AiReleaseRankJudgeTests")]
    [Trait("Category", "Search")]
    public class AiReleaseRankJudgeTests : BaseTests
    {
        private const long Mb = 1024L * 1024;
        private static readonly IReadOnlySet<int> ValidIndexes = new HashSet<int> { 0, 1, 2 };

        private static readonly AiReleaseRankJudge.TargetInput Target = new(
            "61 Hours",
            new[] { "Lee Child" },
            new[] { "Dick Hill" },
            RuntimeMinutes: 793,
            Abridged: false);

        private static AiReleaseRankJudge.CandidateInput Candidate(
            int index, string title, long sizeMb = 600, int? seeders = 10, string? narrator = null, string type = "torrent") =>
            new(index, title, sizeMb * Mb, seeders, 2, "M4B", type, "Indexer", 30, narrator);

        [Fact]
        public void ParseResponse_RankingSkipsRejectedUnknownAndDuplicateIndexes()
        {
            var response = AiReleaseRankJudge.ParseResponse(
                "{\"reject\":[{\"index\":2,\"reason\":\"e-book pack\"}]," +
                "\"ranking\":[{\"index\":1,\"reason\":\"names the narrator\"},{\"index\":2,\"reason\":\"x\"},{\"index\":9,\"reason\":\"x\"},{\"index\":1,\"reason\":\"dup\"},{\"index\":0,\"reason\":\"fallback\"}]}",
                ValidIndexes);

            Assert.Single(response.Rejects);
            Assert.Equal(new[] { 1, 0 }, response.Ranking.Select(r => r.Index).ToArray());
            Assert.Equal("names the narrator", response.Ranking[0].Reason);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Sure! Here is my ranking.")]
        [InlineData("{\"ranking\":\"best is 1\"}")]
        [InlineData("{broken")]
        public void ParseResponse_Garbage_YieldsNothing(string? response)
        {
            var parsed = AiReleaseRankJudge.ParseResponse(response, ValidIndexes);
            Assert.Empty(parsed.Rejects);
            Assert.Empty(parsed.Ranking);
        }

        [Fact]
        public void BuildUserPrompt_TellsTheModelTheFactsItMustNotGuess()
        {
            var prompt = AiReleaseRankJudge.BuildUserPrompt(Target, new[]
            {
                Candidate(0, "61 Hours - Lee Child [M4B]", sizeMb: 700, seeders: 42),
                Candidate(1, "Lee Child - 61 Hours (Dick Hill)", sizeMb: 650, seeders: 3, type: "usenet"),
            });

            Assert.Contains("narrated by Dick Hill", prompt);
            Assert.Contains("793 minutes", prompt);
            Assert.Contains("expected size roughly", prompt);
            Assert.Contains("0: 61 Hours - Lee Child [M4B] [700 MB; M4B; torrent, 42 seeders, 2 leechers", prompt);
            Assert.Contains("usenet", prompt);
            Assert.DoesNotContain("seeders", prompt.Split('\n').First(l => l.StartsWith("1:")));
        }

        [Fact]
        public void ExpectedSizeBand_ScalesWithRuntime()
        {
            var band = AiReleaseRankJudge.ExpectedSizeBand(100)!.Value;
            Assert.Equal((long)(100 * AiReleaseRankJudge.MinPlausibleMbPerMinute * Mb), band.MinBytes);
            Assert.Equal((long)(100 * AiReleaseRankJudge.MaxPlausibleMbPerMinute * Mb), band.MaxBytes);
            Assert.Null(AiReleaseRankJudge.ExpectedSizeBand(null));
            Assert.Null(AiReleaseRankJudge.ExpectedSizeBand(0));
        }

        [Fact]
        public void ValidateClaims_NarratorClaim_HoldsOnlyWhenPreferredNamesNarratorAndLeaderDoesNot()
        {
            var leader = Candidate(0, "61 Hours - Lee Child [M4B]");
            var preferred = Candidate(1, "Lee Child - 61 Hours (read by Dick Hill)");
            var list = new[] { leader, preferred };

            var claims = AiReleaseRankJudge.ValidateClaims("names the target narrator", preferred, leader, list, Target);
            Assert.Equal(new[] { AiReleaseRankJudge.ClaimNarrator }, claims);

            // Leader also names Hill → no differentiator.
            var leaderWithHill = Candidate(0, "61 Hours - Dick Hill");
            Assert.Empty(AiReleaseRankJudge.ValidateClaims("names the target narrator", preferred, leaderWithHill, list, Target));

            // Claim made but the release does not actually name the narrator.
            var noHill = Candidate(1, "Lee Child - 61 Hours [MP3]");
            Assert.Empty(AiReleaseRankJudge.ValidateClaims("narrator matches", noHill, leader, list, Target));
        }

        [Fact]
        public void ValidateClaims_NarratorClaim_AcceptsIndexerNarratorMetadata()
        {
            var leader = Candidate(0, "61 Hours - Lee Child");
            var preferred = Candidate(1, "61 Hours (Unabridged)", narrator: "Dick Hill");

            var claims = AiReleaseRankJudge.ValidateClaims("listed narrator is Dick Hill", preferred, leader, new[] { leader, preferred }, Target);

            Assert.Contains(AiReleaseRankJudge.ClaimNarrator, claims);
        }

        [Fact]
        public void ValidateClaims_SizeClaim_HoldsOnlyWhenPreferredIsInBandAndLeaderIsNot()
        {
            // 793 min → roughly 357–2062 MB.
            var leader = Candidate(0, "61 Hours - Lee Child", sizeMb: 120);   // suspiciously small
            var preferred = Candidate(1, "61 Hours - Lee Child [M4B]", sizeMb: 640);
            var list = new[] { leader, preferred };

            Assert.Equal(new[] { AiReleaseRankJudge.ClaimSize },
                AiReleaseRankJudge.ValidateClaims("size fits the runtime; the other looks incomplete", preferred, leader, list, Target));

            var leaderInBand = Candidate(0, "61 Hours - Lee Child", sizeMb: 500);
            Assert.Empty(AiReleaseRankJudge.ValidateClaims("better size", preferred, leaderInBand, list, Target));
        }

        [Theory]
        [InlineData(40, 10, 40, true)]    // 4x the leader, best of list
        [InlineData(20, 10, 100, false)]  // double the leader but a straggler next to the best
        [InlineData(2, 0, 2, false)]      // too few in absolute terms
        [InlineData(15, 10, 15, false)]   // not a real advantage
        public void ValidateClaims_SeederClaim_RequiresARealAdvantage(int preferredSeeders, int leaderSeeders, int bestSeeders, bool expected)
        {
            var leader = Candidate(0, "61 Hours A", seeders: leaderSeeders);
            var preferred = Candidate(1, "61 Hours B", seeders: preferredSeeders);
            var best = Candidate(2, "61 Hours C", seeders: bestSeeders);

            var claims = AiReleaseRankJudge.ValidateClaims("far more seeders", preferred, leader, new[] { leader, preferred, best }, Target);

            Assert.Equal(expected, claims.Contains(AiReleaseRankJudge.ClaimSeeders));
        }

        [Fact]
        public void ValidateClaims_EditionClaim_UnabridgedOverAbridgedOnly()
        {
            var leader = Candidate(0, "61 Hours - Lee Child (Abridged)");
            var preferred = Candidate(1, "61 Hours - Lee Child (Unabridged)");
            var list = new[] { leader, preferred };

            Assert.Equal(new[] { AiReleaseRankJudge.ClaimEdition },
                AiReleaseRankJudge.ValidateClaims("unabridged edition", preferred, leader, list, Target));

            var abridgedTarget = Target with { Abridged = true };
            Assert.Empty(AiReleaseRankJudge.ValidateClaims("unabridged edition", preferred, leader, list, abridgedTarget));
        }

        [Fact]
        public void ValidateClaims_ReasonWithoutVerifiableKind_IsEmpty()
        {
            var leader = Candidate(0, "61 Hours - Lee Child");
            var preferred = Candidate(1, "61 Hours - Lee Child (Dick Hill)", seeders: 500);

            // Every fact would hold, but the model did not cite any of them.
            Assert.Empty(AiReleaseRankJudge.ValidateClaims("looks like the best release", preferred, leader, new[] { leader, preferred }, Target));
        }
    }
}
