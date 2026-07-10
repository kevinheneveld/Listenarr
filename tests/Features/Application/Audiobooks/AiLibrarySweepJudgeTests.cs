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
using Listenarr.Application.Audiobooks;

namespace Listenarr.Tests.Features.Application.Audiobooks
{
    public class AiLibrarySweepJudgeTests
    {
        private static readonly IReadOnlySet<int> ValidIds = new HashSet<int> { 10, 20 };

        [Fact]
        public void ParseResponse_FlagsValidIdsWithReasons()
        {
            var verdicts = AiLibrarySweepJudge.ParseResponse(
                "```json\n{\"suspicious\":[{\"id\":20,\"reason\":\"files are a music discography\"}]}\n```",
                ValidIds);

            var verdict = Assert.Single(verdicts);
            Assert.Equal(20, verdict.Id);
            Assert.Equal("files are a music discography", verdict.Reason);
        }

        [Fact]
        public void ParseResponse_HallucinatedId_IsDropped()
        {
            var verdicts = AiLibrarySweepJudge.ParseResponse(
                "{\"suspicious\":[{\"id\":999,\"reason\":\"x\"}]}", ValidIds);

            Assert.Empty(verdicts);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("All records look correct.")]
        [InlineData("{\"suspicious\":{}}")]
        public void ParseResponse_Garbage_FlagsNothing(string? response)
        {
            Assert.Empty(AiLibrarySweepJudge.ParseResponse(response, ValidIds));
        }

        [Fact]
        public void BuildUserPrompt_ContainsRecordDetails()
        {
            var prompt = AiLibrarySweepJudge.BuildUserPrompt(new[]
            {
                new AiLibrarySweepJudge.RecordInput(
                    10, "Dark Sky Paradise", new[] { "Big Sean" }, 12,
                    new[] { "01 Dark Sky (Skyscrapers).mp3", "02 Blessings.mp3" }),
            });

            Assert.Contains("id=10", prompt);
            Assert.Contains("Dark Sky Paradise", prompt);
            Assert.Contains("Big Sean", prompt);
            Assert.Contains("files=12", prompt);
            Assert.Contains("01 Dark Sky (Skyscrapers).mp3", prompt);
        }
    }
}
