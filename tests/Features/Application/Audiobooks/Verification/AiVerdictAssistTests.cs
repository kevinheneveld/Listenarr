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
using Listenarr.Domain.Audiobooks.Enumerations;
using Listenarr.Tests.Common;
using Listenarr.Tests.Mocks;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Application.Audiobooks.Verification
{
    /// <summary>
    /// The AI second opinion on verdicts the matcher could not settle. Fails
    /// closed to the deterministic verdict, promotes only on a confident
    /// match, and can never reach the auto-reject bar on its own.
    /// </summary>
    [Trait("Name", "AiVerdictAssistTests")]
    [Trait("Category", "Verification")]
    public class AiVerdictAssistTests : BaseTests
    {
        private static readonly Audiobook Book = new()
        {
            Id = 127,
            Title = "Changes: The Dresden Files, Book 12",
            Authors = new List<string> { "Jim Butcher" }
        };

        private static VerificationVerdict Uncertain(string transcript = "[opening] Penguin Audio presents Changes by Jim Butcher") => new()
        {
            Outcome = VerificationOutcome.Uncertain,
            Confidence = 0.6,
            Method = "deterministic:whisper-small.en",
            Transcript = transcript
        };

        private static IConfigurationService Settings(bool review, string model = "qwen3.5:9b")
        {
            var config = new Mock<IConfigurationService>();
            config.Setup(c => c.GetApplicationSettingsAsync())
                .ReturnsAsync(new ApplicationSettings { AiAssistReviewVerifications = review, AiAssistModel = model });
            return config.Object;
        }

        private static AiVerdictAssist Assist(string? response, bool configured = true, bool review = true) =>
            new(new AiAssistServiceMock { Configured = configured, Response = response }, Settings(review), NullLogger<AiVerdictAssist>.Instance);

        private const string ConfidentMatch = "{\"decision\":\"match\",\"confidence\":0.93,\"reason\":\"credits name Changes by Jim Butcher\"}";

        [Fact]
        public async Task ReviewAsync_ConfidentMatch_PromotesToMatch()
        {
            var reviewed = await Assist(ConfidentMatch).ReviewAsync(Book, Uncertain());

            Assert.Equal(VerificationOutcome.Match, reviewed.Outcome);
            Assert.Equal(AiVerdictAssist.MaxAiMatchConfidence, reviewed.Confidence);
            Assert.Equal("deterministic:whisper-small.en+ai-review", reviewed.Method);
            Assert.Equal("qwen3.5:9b", reviewed.AiReview!.Model);
            Assert.Equal("credits name Changes by Jim Butcher", reviewed.AiReview.Reason);
        }

        [Fact]
        public async Task ReviewAsync_ConfidentMismatch_FlagsBelowAutoRejectBar()
        {
            var reviewed = await Assist("{\"decision\":\"mismatch\",\"confidence\":0.99,\"reason\":\"credits name Hide and Seek by Fern Michaels\"}")
                .ReviewAsync(Book, Uncertain());

            Assert.Equal(VerificationOutcome.Mismatch, reviewed.Outcome);
            Assert.True(reviewed.Confidence < WrongContentAutoReject.MinConfidence);
            Assert.False(WrongContentAutoReject.ShouldAutoReject(true, VerificationTriggers.Import, reviewed.Outcome, reviewed.Confidence, "Hide and Seek", "Fern Michaels"));
        }

        [Fact]
        public void MismatchCap_StaysBelowAutoRejectThreshold()
        {
            Assert.True(AiVerdictAssist.MaxAiMismatchConfidence < WrongContentAutoReject.MinConfidence);
        }

        [Theory]
        [InlineData("{\"decision\":\"unsure\",\"confidence\":0.95,\"reason\":\"no credits\"}")]
        [InlineData("{\"decision\":\"match\",\"confidence\":0.5,\"reason\":\"maybe\"}")]
        [InlineData("{\"decision\":\"mismatch\",\"confidence\":0.6,\"reason\":\"maybe\"}")]
        public async Task ReviewAsync_Indecisive_KeepsOutcomeButRecordsReview(string response)
        {
            var reviewed = await Assist(response).ReviewAsync(Book, Uncertain());

            Assert.Equal(VerificationOutcome.Uncertain, reviewed.Outcome);
            Assert.Equal(0.6, reviewed.Confidence);
            Assert.Equal("deterministic:whisper-small.en", reviewed.Method);
            Assert.NotNull(reviewed.AiReview);
        }

        [Fact]
        public async Task ReviewAsync_SettingOff_LeavesVerdictUntouched()
        {
            var verdict = Uncertain();

            var reviewed = await Assist(ConfidentMatch, review: false).ReviewAsync(Book, verdict);

            Assert.Same(verdict, reviewed);
        }

        [Fact]
        public async Task ReviewAsync_Unconfigured_LeavesVerdictUntouched()
        {
            var verdict = Uncertain();

            var reviewed = await Assist(ConfidentMatch, configured: false).ReviewAsync(Book, verdict);

            Assert.Same(verdict, reviewed);
        }

        [Fact]
        public async Task ReviewAsync_GarbageAnswer_LeavesVerdictUntouched()
        {
            var verdict = Uncertain();

            var reviewed = await Assist("no idea").ReviewAsync(Book, verdict);

            Assert.Same(verdict, reviewed);
            Assert.Null(reviewed.AiReview);
        }

        [Theory]
        [InlineData(VerificationOutcome.Match)]
        [InlineData(VerificationOutcome.Mismatch)]
        public async Task ReviewAsync_SettledVerdicts_AreNotReviewed(VerificationOutcome outcome)
        {
            var verdict = Uncertain() with { Outcome = outcome };

            var reviewed = await Assist(ConfidentMatch).ReviewAsync(Book, verdict);

            Assert.Same(verdict, reviewed);
        }

        [Fact]
        public async Task ReviewAsync_NoSpokenCredits_IsReviewed()
        {
            var verdict = Uncertain() with { Outcome = VerificationOutcome.NoSpokenCredits };

            var reviewed = await Assist(ConfidentMatch).ReviewAsync(Book, verdict);

            Assert.Equal(VerificationOutcome.Match, reviewed.Outcome);
        }

        [Fact]
        public async Task ReviewAsync_ConfidentMatchWithoutCreditEvidence_IsRecordedButNotPromoted()
        {
            // Live: "Blaze" opens straight into narration; the model recognised
            // the story and answered match 0.95. No credits heard, no field
            // the matcher half-recognised — the outcome must not move.
            var narrativeOnly = Uncertain("[opening] George was somewhere in the dark. Blaze couldn't see, but the voice came in loud and clear.") with
            {
                Confidence = 0.262,
                TitleMatch = new VerificationFieldMatch(0, null),
                AuthorMatch = new VerificationFieldMatch(0.425, "began cooing"),
                HeardCredits = new SpokenCredits()
            };

            var reviewed = await Assist(ConfidentMatch).ReviewAsync(Book, narrativeOnly);

            Assert.Equal(VerificationOutcome.Uncertain, reviewed.Outcome);
            Assert.Equal(0.262, reviewed.Confidence);
            Assert.Equal("match", reviewed.AiReview!.Decision);
        }

        [Fact]
        public void HasCreditEvidence_AcceptsMarkersHeardCreditsOrHalfRecognisedFields()
        {
            var bare = Uncertain("[opening] plain narration with nothing announced") with { TitleMatch = new VerificationFieldMatch(0.1, null) };
            Assert.False(AiVerdictAssist.HasCreditEvidence(bare));
            Assert.True(AiVerdictAssist.HasCreditEvidence(bare with { Transcript = "[opening] Macmillan Audio presents nothing in particular" }));
            Assert.True(AiVerdictAssist.HasCreditEvidence(bare with { HeardCredits = new SpokenCredits { Author = "Fern Michaels" } }));
            Assert.True(AiVerdictAssist.HasCreditEvidence(bare with { AuthorMatch = new VerificationFieldMatch(0.5, "robert heinlein") }));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("[inconclusive: clip extraction produced no audio windows]")]
        public void ShouldReview_NothingToRead_IsFalse(string? transcript)
        {
            Assert.False(AiVerdictAssist.ShouldReview(Uncertain() with { Transcript = transcript }));
        }

        [Fact]
        public void DetailSerializer_RoundTripsAStoredVerdictWithReview()
        {
            var reviewed = AiVerdictAssist.Apply(
                Uncertain(),
                new VerificationAiReview("match", 0.9, "credits say so", "qwen3.5:9b"));

            var json = VerificationDetailSerializer.Serialize(reviewed);
            var back = VerificationDetailSerializer.TryDeserialize(json);

            Assert.Contains("\"aiReview\"", json);
            Assert.DoesNotContain("transcript", json);
            Assert.NotNull(back);
            Assert.Equal(VerificationOutcome.Match, back!.Outcome);
            Assert.Equal("match", back.AiReview!.Decision);
            Assert.Null(back.Transcript);
        }

        [Fact]
        public void DetailSerializer_ReadsAVerdictWrittenBeforeAiReviewExisted()
        {
            const string legacy = "{\"outcome\":\"uncertain\",\"confidence\":0.6,\"method\":\"deterministic:whisper-small.en\",\"titleMatch\":{\"score\":0.167},\"authorMatch\":{\"score\":1,\"matchedText\":\"jim butcher\"}}";

            var back = VerificationDetailSerializer.TryDeserialize(legacy);

            Assert.NotNull(back);
            Assert.Equal(VerificationOutcome.Uncertain, back!.Outcome);
            Assert.Equal(1, back.AuthorMatch!.Score);
            Assert.Null(back.AiReview);
            Assert.Null(VerificationDetailSerializer.TryDeserialize("{not json"));
        }
    }
}
