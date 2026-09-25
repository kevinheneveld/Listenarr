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
using Listenarr.Application.Audiobooks.Verification;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Audiobooks.Verification
{
    [Trait("Name", "AiVerdictJudgeTests")]
    [Trait("Category", "Verification")]
    public class AiVerdictJudgeTests : BaseTests
    {
        [Fact]
        public void ParseResponse_ReadsDecisionConfidenceAndReason()
        {
            var review = AiVerdictJudge.ParseResponse(
                "{\"decision\":\"match\",\"confidence\":0.92,\"reason\":\"credits say Changes by Jim Butcher\"}", "qwen3.5:9b");

            Assert.NotNull(review);
            Assert.Equal("match", review!.Decision);
            Assert.Equal(0.92, review.Confidence, 3);
            Assert.Equal("credits say Changes by Jim Butcher", review.Reason);
            Assert.Equal("qwen3.5:9b", review.Model);
        }

        [Fact]
        public void ParseResponse_ToleratesFencesAndPercentConfidence()
        {
            var review = AiVerdictJudge.ParseResponse(
                "```json\n{\"decision\":\"MISMATCH\",\"confidence\":\"85\",\"reason\":\"credits name Fern Michaels\"}\n```", null);

            Assert.NotNull(review);
            Assert.Equal("mismatch", review!.Decision);
            Assert.Equal(0.85, review.Confidence, 3);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("I think it matches.")]
        [InlineData("{\"decision\":\"maybe\",\"confidence\":0.9}")]
        [InlineData("{\"verdict\":\"match\"}")]
        [InlineData("{oops")]
        public void ParseResponse_Garbage_ReturnsNull(string? response)
        {
            Assert.Null(AiVerdictJudge.ParseResponse(response, "m"));
        }

        [Fact]
        public void BuildUserPrompt_ContainsMetadataAndTranscript()
        {
            var book = new Audiobook
            {
                Title = "Changes",
                Subtitle = "The Dresden Files, Book 12",
                Authors = new List<string> { "Jim Butcher" },
                Narrators = new List<string> { "James Marsters" },
                Series = "The Dresden Files",
                SeriesNumber = "12"
            };

            var prompt = AiVerdictJudge.BuildUserPrompt(book, "[opening] Penguin Audio presents Changes by Jim Butcher, read by James Marsters");

            Assert.Contains("Title: Changes", prompt);
            Assert.Contains("Subtitle: The Dresden Files, Book 12", prompt);
            Assert.Contains("Author(s): Jim Butcher", prompt);
            Assert.Contains("Narrator(s) on record (other editions may differ): James Marsters", prompt);
            Assert.Contains("Series: The Dresden Files #12", prompt);
            Assert.Contains("read by James Marsters", prompt);
        }

        [Fact]
        public void BuildUserPrompt_TruncatesLongTranscripts()
        {
            var transcript = new string('x', AiVerdictJudge.MaxTranscriptChars * 3);

            var prompt = AiVerdictJudge.BuildUserPrompt(new Audiobook { Title = "T" }, transcript);

            Assert.True(prompt.Length < AiVerdictJudge.MaxTranscriptChars + 200);
        }
    }
}
