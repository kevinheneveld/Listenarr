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

namespace Listenarr.Tests.Features.Application.Search
{
    public class AiReleaseGateJudgeTests
    {
        private static readonly IReadOnlySet<int> ValidIndexes = new HashSet<int> { 0, 1, 2 };

        [Fact]
        public void ParseResponse_FlagsValidIndexesWithReasons()
        {
            var verdicts = AiReleaseGateJudge.ParseResponse(
                "{\"reject\":[{\"index\":1,\"reason\":\"music album\"}]}", ValidIndexes);

            var verdict = Assert.Single(verdicts);
            Assert.Equal(1, verdict.Index);
            Assert.Equal("music album", verdict.Reason);
        }

        [Fact]
        public void ParseResponse_UnknownIndex_IsDropped()
        {
            var verdicts = AiReleaseGateJudge.ParseResponse(
                "{\"reject\":[{\"index\":7,\"reason\":\"made up\"},{\"index\":0,\"reason\":\"different book\"}]}",
                ValidIndexes);

            var verdict = Assert.Single(verdicts);
            Assert.Equal(0, verdict.Index);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("Everything looks fine to me!")]
        [InlineData("{\"reject\":\"none\"}")]
        [InlineData("{oops")]
        public void ParseResponse_Garbage_FailsOpen(string? response)
        {
            Assert.Empty(AiReleaseGateJudge.ParseResponse(response, ValidIndexes));
        }

        [Fact]
        public void BuildUserPrompt_ContainsTargetAndNumberedReleases()
        {
            var prompt = AiReleaseGateJudge.BuildUserPrompt(
                "The Shattering Peace",
                new[] { "John Scalzi" },
                618,
                new[]
                {
                    new AiReleaseGateJudge.ReleaseInput(0, "John Scalzi - The Shattering Peace (Unabr)", 500L * 1024 * 1024),
                    new AiReleaseGateJudge.ReleaseInput(1, "Big Sean-Dark Sky Paradise-CD-FLAC-2015-FATHEAD", 400L * 1024 * 1024),
                });

            Assert.Contains("The Shattering Peace", prompt);
            Assert.Contains("John Scalzi", prompt);
            Assert.Contains("618 minutes", prompt);
            Assert.Contains("0: John Scalzi - The Shattering Peace (Unabr) [500 MB]", prompt);
            Assert.Contains("1: Big Sean-Dark Sky Paradise", prompt);
        }

        [Fact]
        public void BuildUserPrompt_CapsAtMaxCandidates()
        {
            var releases = Enumerable.Range(0, AiReleaseGateJudge.MaxCandidates + 3)
                .Select(i => new AiReleaseGateJudge.ReleaseInput(i, $"Release {i}", null))
                .ToList();

            var prompt = AiReleaseGateJudge.BuildUserPrompt("T", Array.Empty<string>(), null, releases);

            Assert.Contains($"{AiReleaseGateJudge.MaxCandidates - 1}: Release", prompt);
            Assert.DoesNotContain($"{AiReleaseGateJudge.MaxCandidates}: Release", prompt);
        }
    }
}
