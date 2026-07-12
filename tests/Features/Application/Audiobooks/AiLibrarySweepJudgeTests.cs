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
        private static readonly IReadOnlyDictionary<int, AiLibrarySweepJudge.RecordEvidence> FileNames =
            new Dictionary<int, AiLibrarySweepJudge.RecordEvidence>
            {
                [10] = new(new[] { "01 Dark Sky (Skyscrapers).mp3", "02 Blessings.mp3" }, null),
                [20] = new(new[] { "Dragon Tear.mp3" }, null),
                [30] = new(
                    new[] { "The Black Book-001.mp3" },
                    "[opening] Recording Books Romance presents an unabridged recording of Dark Lover by J.R. Ward, narrated by Jim Frangione."),
                [40] = new(
                    new[] { "Hunted Down.m4b" },
                    "[opening] (eerie music) ♪♪♪ ♪♪♪ (eerie music) [Music]\n[closing] (dramatic music) ♪♪ ♪♪"),
            };

        [Fact]
        public void ParseResponse_ValidIdWithRealEvidence_Flags()
        {
            var verdicts = AiLibrarySweepJudge.ParseResponse(
                "```json\n{\"suspicious\":[{\"id\":10,\"evidence\":\"02 Blessings.mp3\",\"reason\":\"Big Sean album tracks\"}]}\n```",
                FileNames);

            var verdict = Assert.Single(verdicts);
            Assert.Equal(10, verdict.Id);
            Assert.Equal("02 Blessings.mp3", verdict.Evidence);
            Assert.Equal("Big Sean album tracks", verdict.Reason);
        }

        [Fact]
        public void ParseResponse_EvidenceNotInRecordsFiles_IsDropped()
        {
            // A flag whose "proof" doesn't exist among the record's submitted
            // file names is fabricated — the first live run produced reasons
            // like "file format is not .mp3" about a file that literally was
            // Dragon Tear.mp3. The evidence gate kills that class of answer.
            var verdicts = AiLibrarySweepJudge.ParseResponse(
                "{\"suspicious\":[{\"id\":20,\"evidence\":\"Dragon Tear.flac\",\"reason\":\"file format is not .mp3\"}]}",
                FileNames);

            Assert.Empty(verdicts);
        }

        [Fact]
        public void ParseResponse_MissingEvidence_IsDropped()
        {
            var verdicts = AiLibrarySweepJudge.ParseResponse(
                "{\"suspicious\":[{\"id\":20,\"reason\":\"looks wrong\"}]}",
                FileNames);

            Assert.Empty(verdicts);
        }

        [Fact]
        public void ParseResponse_HallucinatedId_IsDropped()
        {
            var verdicts = AiLibrarySweepJudge.ParseResponse(
                "{\"suspicious\":[{\"id\":999,\"evidence\":\"Dragon Tear.mp3\",\"reason\":\"x\"}]}",
                FileNames);

            Assert.Empty(verdicts);
        }

        [Fact]
        public void ParseResponse_EvidenceMatchIsCaseInsensitive()
        {
            var verdicts = AiLibrarySweepJudge.ParseResponse(
                "{\"suspicious\":[{\"id\":20,\"evidence\":\"dragon tear.MP3\",\"reason\":\"different book\"}]}",
                FileNames);

            Assert.Single(verdicts);
        }

        [Fact]
        public void ParseResponse_TranscriptFragmentAsEvidence_Flags()
        {
            // The mislabeled "Black Book"/Dark Lover live case: the whisper
            // opening names a different book, and quoting that credit phrase
            // is valid evidence.
            var verdicts = AiLibrarySweepJudge.ParseResponse(
                "{\"suspicious\":[{\"id\":30,\"evidence\":\"Dark Lover by J.R. Ward\",\"reason\":\"audio announces Dark Lover, not The Black Book\"}]}",
                FileNames);

            var verdict = Assert.Single(verdicts);
            Assert.Equal(30, verdict.Id);
        }

        [Fact]
        public void ParseResponse_FabricatedTranscriptQuote_IsDropped()
        {
            var verdicts = AiLibrarySweepJudge.ParseResponse(
                "{\"suspicious\":[{\"id\":30,\"evidence\":\"Moby Dick by Herman Melville\",\"reason\":\"wrong book\"}]}",
                FileNames);

            Assert.Empty(verdicts);
        }

        [Fact]
        public void ParseResponse_TinyTranscriptFragment_IsDropped()
        {
            // A trivially-short quote ("the") appears in every transcript and
            // proves nothing; substance is required.
            var verdicts = AiLibrarySweepJudge.ParseResponse(
                "{\"suspicious\":[{\"id\":30,\"evidence\":\"recording\",\"reason\":\"x\"}]}",
                FileNames);

            Assert.Empty(verdicts);
        }

        [Fact]
        public void ParseResponse_MusicNotationEvidence_ShortButProbative_Flags()
        {
            // Live miss: a transcript that is nothing but music cues/♪ gives
            // the model no 12-char lyric line to quote — the note glyphs ARE
            // the evidence, and they only appear when whisper heard singing.
            var verdicts = AiLibrarySweepJudge.ParseResponse(
                "{\"suspicious\":[{\"id\":40,\"evidence\":\"♪♪♪\",\"reason\":\"transcript is music, not narration\"}]}",
                FileNames);

            var verdict = Assert.Single(verdicts);
            Assert.Equal(40, verdict.Id);
        }

        [Fact]
        public void ParseResponse_MusicAnnotationQuote_Flags()
        {
            var verdicts = AiLibrarySweepJudge.ParseResponse(
                "{\"suspicious\":[{\"id\":40,\"evidence\":\"(eerie music)\",\"reason\":\"music track\"}]}",
                FileNames);

            Assert.Single(verdicts);
        }

        [Fact]
        public void ParseResponse_FabricatedMusicGlyphQuote_IsDropped()
        {
            // ♪ relaxes the length floor, not the substring requirement —
            // record 30's transcript has no note glyphs, so this is invented.
            var verdicts = AiLibrarySweepJudge.ParseResponse(
                "{\"suspicious\":[{\"id\":30,\"evidence\":\"♪♪♪\",\"reason\":\"music\"}]}",
                FileNames);

            Assert.Empty(verdicts);
        }

        [Fact]
        public void BuildSystemPrompt_MakesMusicTranscriptsFlaggable()
        {
            // The first prompt hardening over-corrected: "audio-opening with
            // no credits is normal" read as never-flag for lyric/♪ openings
            // too, and live music records sailed through. Music-as-opening
            // must be an explicit flag category.
            var prompt = AiLibrarySweepJudge.BuildSystemPrompt();
            Assert.Contains("song lyrics", prompt);
            Assert.Contains("♪", prompt);
            Assert.Contains("ordinary spoken prose with no credits", prompt);
        }

        [Fact]
        public void BuildUserPrompt_IncludesTranscriptWhenPresent()
        {
            var prompt = AiLibrarySweepJudge.BuildUserPrompt(new[]
            {
                new AiLibrarySweepJudge.RecordInput(
                    30, "The Black Book", new[] { "James Patterson" }, 1,
                    new[] { "The Black Book-001.mp3" },
                    "Recording Books Romance presents Dark Lover by J.R. Ward"),
            });

            Assert.Contains("audio-opening:", prompt);
            Assert.Contains("Dark Lover by J.R. Ward", prompt);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("All records look correct.")]
        [InlineData("{\"suspicious\":{}}")]
        public void ParseResponse_Garbage_FlagsNothing(string? response)
        {
            Assert.Empty(AiLibrarySweepJudge.ParseResponse(response, FileNames));
        }

        [Fact]
        public void BuildSystemPrompt_ListsTheNonReasons()
        {
            // The first live run invented exactly these criteria; the prompt
            // must keep disclaiming them explicitly.
            var prompt = AiLibrarySweepJudge.BuildSystemPrompt();
            Assert.Contains("file extension or format", prompt);
            Assert.Contains("how many files", prompt);
            Assert.Contains("empty list is the expected answer", prompt);
            Assert.Contains("evidence", prompt);
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
            Assert.Contains("01 Dark Sky (Skyscrapers).mp3", prompt);
        }
    }
}
