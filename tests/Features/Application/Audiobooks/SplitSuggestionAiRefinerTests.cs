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
    public class SplitSuggestionAiRefinerTests
    {
        private static readonly IReadOnlySet<int> ValidIds = new HashSet<int> { 100, 200, 300 };

        [Fact]
        public void ParseResponse_CleanJson_MapsKeysToTargets()
        {
            var parsed = SplitSuggestionAiRefiner.ParseResponse(
                "{\"matches\":[{\"key\":\"dir:Rama\",\"targetId\":100},{\"key\":\"dir:Unknown\",\"targetId\":null}]}",
                ValidIds);

            Assert.Equal(2, parsed.Count);
            Assert.Equal(100, parsed["dir:Rama"]);
            Assert.Null(parsed["dir:Unknown"]);
        }

        [Fact]
        public void ParseResponse_MarkdownFencesAndProse_StillParses()
        {
            // Local models routinely wrap JSON in fences or lead-in prose
            // despite instructions; the parser must find the object anyway.
            var parsed = SplitSuggestionAiRefiner.ParseResponse(
                "Sure! Here is the mapping:\n```json\n{\"matches\":[{\"key\":\"dir:Rama\",\"targetId\":200}]}\n```",
                ValidIds);

            Assert.Single(parsed);
            Assert.Equal(200, parsed["dir:Rama"]);
        }

        [Fact]
        public void ParseResponse_HallucinatedId_IsDropped()
        {
            // An id not in the candidate list must never become a suggestion —
            // the model invented it.
            var parsed = SplitSuggestionAiRefiner.ParseResponse(
                "{\"matches\":[{\"key\":\"dir:Rama\",\"targetId\":9999},{\"key\":\"dir:Real\",\"targetId\":300}]}",
                ValidIds);

            Assert.Single(parsed);
            Assert.Equal(300, parsed["dir:Real"]);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("I could not determine any matches.")]
        [InlineData("{\"matches\": \"not an array\"}")]
        [InlineData("{broken json")]
        public void ParseResponse_Garbage_ReturnsEmpty(string? response)
        {
            Assert.Empty(SplitSuggestionAiRefiner.ParseResponse(response, ValidIds));
        }

        [Fact]
        public void BuildUserPrompt_ContainsCandidatesGroupsAndSource()
        {
            var prompt = SplitSuggestionAiRefiner.BuildUserPrompt(
                "A Fall of Moondust",
                new[] { "Arthur C. Clarke" },
                new[]
                {
                    new SplitSuggestionAiRefiner.ClusterInput(
                        "dir:Rama", "Rama", new[] { "Rendezvous with Rama Part 1 of 16.mp3" }, null),
                },
                new[] { new SplitSuggestionAiRefiner.CandidateInput(100, "Rendezvous with Rama") });

            Assert.Contains("A Fall of Moondust", prompt);
            Assert.Contains("Arthur C. Clarke", prompt);
            Assert.Contains("100: Rendezvous with Rama", prompt);
            Assert.Contains("key=dir:Rama", prompt);
            Assert.Contains("Rendezvous with Rama Part 1 of 16.mp3", prompt);
        }

        [Fact]
        public void BuildUserPrompt_CapsCandidateList()
        {
            var candidates = Enumerable.Range(1, SplitSuggestionAiRefiner.MaxCandidates + 50)
                .Select(i => new SplitSuggestionAiRefiner.CandidateInput(i, $"Book {i}"))
                .ToList();

            var prompt = SplitSuggestionAiRefiner.BuildUserPrompt(
                "Source", Array.Empty<string>(), Array.Empty<SplitSuggestionAiRefiner.ClusterInput>(), candidates);

            Assert.Contains($"{SplitSuggestionAiRefiner.MaxCandidates}: Book {SplitSuggestionAiRefiner.MaxCandidates}", prompt);
            Assert.DoesNotContain($"{SplitSuggestionAiRefiner.MaxCandidates + 1}: Book", prompt);
        }
    }
}
