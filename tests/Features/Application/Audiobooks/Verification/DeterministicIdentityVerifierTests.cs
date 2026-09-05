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
using Listenarr.Application.Audiobooks.Verification.Contracts;

using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Listenarr.Tests.Features.Application.Audiobooks.Verification
{
    public class DeterministicIdentityVerifierTests
    {
        private static DeterministicIdentityVerifier CreateVerifier()
        {
            var whisper = new Mock<IWhisperService>();
            whisper.SetupGet(w => w.ModelName).Returns("base.en");

            return new DeterministicIdentityVerifier(
                Mock.Of<IAudioSampleExtractor>(),
                whisper.Object,
                Mock.Of<IConfigurationService>(),
                NullLogger<DeterministicIdentityVerifier>.Instance);
        }

        private static Audiobook HailMary() => new()
        {
            Id = 1,
            Title = "Project Hail Mary",
            Authors = new List<string> { "Andy Weir" },
            Narrators = new List<string> { "Ray Porter" },
            Publisher = "Audible Studios"
        };

        private static Audiobook ExForce(string title, string? series = "Expeditionary Force") => new()
        {
            Id = 2,
            Title = title,
            Series = series,
            Authors = new List<string> { "Craig Alanson" },
            Narrators = new List<string> { "R.C. Bray" },
            Publisher = "Podium Audio"
        };

        [Fact]
        public void Evaluate_TitleHeardOnlyInsideSeriesPhrase_IsNotAMatch()
        {
            // Live case: a 99-file series dump on the record "Mavericks". The
            // opening announces Death Trap, BOOK ONE OF Expeditionary Force
            // Mavericks — "mavericks" is the sub-series name, not this book.
            var verdict = CreateVerifier().Evaluate(
                ExForce("Mavericks"),
                "This is Audible. Blue Heron Audio presents Death Trap, Book One of Expeditionary Force Mavericks, " +
                "by Craig Alanson, performed by R.C. Bray. Chapter one.",
                closingText: null);

            Assert.NotEqual(VerificationOutcome.Match, verdict.Outcome);
            Assert.NotNull(verdict.TitleMatch);
            Assert.True(verdict.TitleMatch!.Score < 0.75, $"title score {verdict.TitleMatch.Score}");
            Assert.StartsWith("series phrase only", verdict.TitleMatch.MatchedText);
        }

        [Fact]
        public void Evaluate_SeriesNameEqualsRecordTitle_IsNotAMatch()
        {
            // Second live case: "Recon, Book Four of Convergence" on the record
            // titled "Convergence" (48 h of audio on a 17 h book).
            var verdict = CreateVerifier().Evaluate(
                ExForce("Convergence", series: "Convergence"),
                "Recon, Book Four of Convergence, written by Craig Alanson, narrated by R.C. Bray.",
                closingText: null);

            Assert.NotEqual(VerificationOutcome.Match, verdict.Outcome);
        }

        [Fact]
        public void Evaluate_TitleHeardBeforeSeriesPhrase_StillMatches()
        {
            // The ordinary shape: the real title precedes the series phrase.
            var verdict = CreateVerifier().Evaluate(
                ExForce("Fallout"),
                "Podium Audio presents Fallout, Book 13 of Expeditionary Force, written by Craig Alanson, performed by R.C. Bray.",
                closingText: null);

            Assert.Equal(VerificationOutcome.Match, verdict.Outcome);
            Assert.Equal(1.0, verdict.TitleMatch!.Score);
        }

        [Fact]
        public void Evaluate_SubSeriesBookOne_StillMatchesItsOwnTitle()
        {
            // Deathtrap IS Mavericks book one: its title stands outside the
            // phrase, so the guard must leave it alone.
            var verdict = CreateVerifier().Evaluate(
                ExForce("Deathtrap", series: "Expeditionary Force Mavericks"),
                "Blue Heron Audio presents Deathtrap, Book One of Expeditionary Force Mavericks, by Craig Alanson, performed by R.C. Bray.",
                closingText: null);

            Assert.Equal(VerificationOutcome.Match, verdict.Outcome);
        }

        [Fact]
        public void DiscountSeriesPhraseTitleMatch_NoSeriesPhrase_ReturnsInputUnchanged()
        {
            var input = new VerificationFieldMatch(1.0, "mavericks");
            var result = DeterministicIdentityVerifier.DiscountSeriesPhraseTitleMatch(
                input, "Mavericks by Craig Alanson, performed by R.C. Bray.", "Mavericks");
            Assert.Same(input, result);
        }

        [Fact]
        public void Evaluate_CleanCredits_IsMatch()
        {
            var verdict = CreateVerifier().Evaluate(
                HailMary(),
                "Audible presents Project Hail Mary by Andy Weir narrated by Ray Porter",
                closingText: null);

            Assert.Equal(VerificationOutcome.Match, verdict.Outcome);
            Assert.True(verdict.Confidence >= 0.7, $"confidence {verdict.Confidence}");
            Assert.NotNull(verdict.TitleMatch);
            Assert.NotNull(verdict.AuthorMatch);
            Assert.Equal("deterministic:whisper-base.en", verdict.Method);
        }

        [Fact]
        public void Evaluate_WrongContent_IsHighConfidenceMismatch()
        {
            // Music-scene release imported as an audiobook: plenty of speech,
            // zero trace of title or author — the gross mismatch this feature
            // exists to catch, and the HIGH-confidence signal per the ADR.
            var verdict = CreateVerifier().Evaluate(
                HailMary(),
                "thank you for listening to the midnight sessions volume four " +
                "all tracks composed and performed by the velvet underground revival band",
                closingText: null);

            Assert.Equal(VerificationOutcome.Mismatch, verdict.Outcome);
            Assert.True(verdict.Confidence >= 0.7, $"gross mismatch should be confident, got {verdict.Confidence}");
        }

        [Fact]
        public void Evaluate_PureNarrationWithoutCredits_IsNoSpokenCredits_NotMismatch()
        {
            // The top live false-flag: a book that opens with pure narration.
            // Plenty of clear speech, zero announcement-shaped phrases — the
            // audio simply never says what it is. That is NOT evidence of wrong
            // content and must not land in the flagged bucket.
            var verdict = CreateVerifier().Evaluate(
                HailMary(),
                "the rain had not stopped for three days and the river kept rising " +
                "past the old stone markers so she finally decided to leave the " +
                "cabin and walk down the mountain toward the village because " +
                "her sister still kept a lamp burning in the front room every night",
                closingText: null);

            Assert.Equal(VerificationOutcome.NoSpokenCredits, verdict.Outcome);
            Assert.True(verdict.Confidence <= 0.4, $"neutral outcome should not be confident, got {verdict.Confidence}");
        }

        [Fact]
        public void Evaluate_ContradictingSpokenCredits_IsStillMismatch()
        {
            // The audio ANNOUNCES a different book — structured credits that
            // contradict the stored metadata. This must remain the confident
            // mismatch the feature exists to catch.
            var verdict = CreateVerifier().Evaluate(
                HailMary(),
                "blackstone audio presents the quiet gardener by margaret holloway " +
                "narrated by susan fields this recording is copyrighted 2011 by blackstone audio",
                closingText: null);

            Assert.Equal(VerificationOutcome.Mismatch, verdict.Outcome);
            Assert.True(verdict.Confidence >= 0.7, $"contradicting credits should stay confident, got {verdict.Confidence}");
        }

        [Fact]
        public void StatusFor_NoSpokenCredits_MapsToAgentUnverifiable()
        {
            Assert.Equal(
                VerificationStatus.AgentUnverifiable,
                Listenarr.Infrastructure.HostedServices.Verification.LibraryVerificationBackgroundService.StatusFor(VerificationOutcome.NoSpokenCredits));
            Assert.True(VerificationStatus.AgentUnverifiable.IsAgentWritable());
        }

        [Fact]
        public void Evaluate_TooLittleSpeech_IsUncertainNeverMismatch()
        {
            // Music intro / silence yields a near-empty transcript: no signal
            // either way. Must land in Uncertain (low confidence), never Mismatch.
            var verdict = CreateVerifier().Evaluate(HailMary(), "la la music sounds", closingText: null);

            Assert.Equal(VerificationOutcome.Uncertain, verdict.Outcome);
            Assert.True(verdict.Confidence <= 0.3);
        }

        [Fact]
        public void Evaluate_MangledTitleButClearAuthor_IsMatch()
        {
            // STT garbles an unusual title word but nails the author name —
            // common on invented words; author corroboration carries it.
            var verdict = CreateVerifier().Evaluate(
                HailMary(),
                "project hale mary written by andy weir and narrated by ray porter for your enjoyment",
                closingText: null);

            Assert.Equal(VerificationOutcome.Match, verdict.Outcome);
        }

        [Fact]
        public void Evaluate_PartialSignal_IsUncertain()
        {
            // Title half-present, author absent: ambiguous, goes to human review.
            var verdict = CreateVerifier().Evaluate(
                HailMary(),
                "welcome to this recording of the mary chronicles read aloud for you by your favorite narrator today",
                closingText: null);

            Assert.Equal(VerificationOutcome.Uncertain, verdict.Outcome);
        }

        [Fact]
        public void Evaluate_MangledAuthorButExactTitleAndNarrator_IsMatch()
        {
            // Observed live on A Court of Thorns and Roses: title, narrator, and
            // publisher all exact but the author rendering imperfect — the
            // narrator corroboration path must accept this, not flag it.
            var book = new Audiobook
            {
                Id = 3,
                Title = "A Court of Thorns and Roses",
                Authors = new List<string> { "Sarah Q. Maazzz" }, // unmatchable rendering
                Narrators = new List<string> { "Jennifer Ikeda" }
            };

            var verdict = CreateVerifier().Evaluate(
                book,
                "a court of thorns and roses written by somebody unclear narrated by jennifer ikeda",
                closingText: null);

            Assert.Equal(VerificationOutcome.Match, verdict.Outcome);
        }

        [Fact]
        public void Evaluate_ClosingWindowCredits_CountTowardMatch()
        {
            var verdict = CreateVerifier().Evaluate(
                HailMary(),
                openingText: "previously on the unrelated publisher preview of some other novel entirely",
                closingText: "this has been Project Hail Mary by Andy Weir narrated by Ray Porter");

            Assert.Equal(VerificationOutcome.Match, verdict.Outcome);
            Assert.Contains("[closing]", verdict.Transcript);
        }

        [Fact]
        public void Evaluate_BookWithoutNarratorOrPublisher_LeavesFieldsNull()
        {
            var book = new Audiobook { Id = 2, Title = "The Martian", Authors = new List<string> { "Andy Weir" } };

            var verdict = CreateVerifier().Evaluate(
                book, "the martian by andy weir read by someone great today", closingText: null);

            Assert.Null(verdict.NarratorMatch);
            Assert.Null(verdict.PublisherMatch);
            Assert.Equal(VerificationOutcome.Match, verdict.Outcome);
        }

        // --- Two-tier cascade -------------------------------------------------

        [Theory]
        [InlineData(VerificationOutcome.Uncertain, "small.en", true)]
        [InlineData(VerificationOutcome.NoSpokenCredits, "small.en", true)]
        [InlineData(VerificationOutcome.Mismatch, "small.en", true)]
        [InlineData(VerificationOutcome.Match, "small.en", false)]        // confident: no escalation
        [InlineData(VerificationOutcome.Uncertain, "", false)]            // disabled
        [InlineData(VerificationOutcome.Uncertain, "   ", false)]         // disabled (whitespace)
        [InlineData(VerificationOutcome.Uncertain, null, false)]          // disabled (null)
        [InlineData(VerificationOutcome.Uncertain, "base.en", false)]     // same model: pointless
        [InlineData(VerificationOutcome.Uncertain, "BASE.EN", false)]     // same model, case-insensitive
        public void ShouldEscalate_Matrix(VerificationOutcome outcome, string? model, bool expected)
        {
            Assert.Equal(expected, DeterministicIdentityVerifier.ShouldEscalate(outcome, model, "base.en"));
        }

        [Fact]
        public void ComposeEscalatedMethod_RecordsCascadePath()
        {
            Assert.Equal(
                "deterministic:whisper-base.en→small.en",
                DeterministicIdentityVerifier.ComposeEscalatedMethod("base.en", " small.en "));
        }

        [Fact]
        public async Task VerifyAsync_EscalatesInconclusiveFirstPass_AndUsesEscalatedVerdict()
        {
            // First pass (base.en) garbles the credits into an uncertain read;
            // the escalated model hears them cleanly — the Before Eden shape.
            var whisper = new Mock<IWhisperService>();
            whisper.SetupGet(w => w.ModelName).Returns("base.en");
            whisper.Setup(w => w.TranscribeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("closing even by author c clock some story text follows here and continues on for a while longer");
            whisper.Setup(w => w.TranscribeWithModelAsync(It.IsAny<string>(), "small.en", It.IsAny<CancellationToken>()))
                .ReturnsAsync("Project Hail Mary by Andy Weir narrated by Ray Porter");

            var samples = new AudioSampleSet { OpeningClipPath = "/tmp/opening.wav" };
            var extractor = new Mock<IAudioSampleExtractor>();
            extractor.Setup(e => e.ExtractAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<AudioSampleStrategy>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(samples);

            var configuration = new Mock<IConfigurationService>();
            configuration.Setup(c => c.GetApplicationSettingsAsync())
                .ReturnsAsync(new ApplicationSettings { VerificationEscalationModel = "small.en" });

            var verifier = new DeterministicIdentityVerifier(
                extractor.Object, whisper.Object, configuration.Object,
                NullLogger<DeterministicIdentityVerifier>.Instance);

            var verdict = await verifier.VerifyAsync(HailMary(), "/tmp/book.mp3");

            Assert.Equal(VerificationOutcome.Match, verdict.Outcome);
            Assert.Equal("deterministic:whisper-base.en→small.en", verdict.Method);
            whisper.Verify(w => w.TranscribeWithModelAsync(It.IsAny<string>(), "small.en", It.IsAny<CancellationToken>()), Times.Once);
        }

        [Fact]
        public async Task VerifyAsync_EscalationUnavailable_KeepsFirstPassVerdict()
        {
            // TranscribeWithModelAsync returning null = model missing/undownloadable;
            // the first-pass verdict (and its method string) must survive untouched.
            var whisper = new Mock<IWhisperService>();
            whisper.SetupGet(w => w.ModelName).Returns("base.en");
            whisper.Setup(w => w.TranscribeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("closing even by author c clock some story text follows here and continues on for a while longer");
            whisper.Setup(w => w.TranscribeWithModelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((string?)null);

            var samples = new AudioSampleSet { OpeningClipPath = "/tmp/opening.wav" };
            var extractor = new Mock<IAudioSampleExtractor>();
            extractor.Setup(e => e.ExtractAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<AudioSampleStrategy>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(samples);

            var configuration = new Mock<IConfigurationService>();
            configuration.Setup(c => c.GetApplicationSettingsAsync())
                .ReturnsAsync(new ApplicationSettings { VerificationEscalationModel = "small.en" });

            var verifier = new DeterministicIdentityVerifier(
                extractor.Object, whisper.Object, configuration.Object,
                NullLogger<DeterministicIdentityVerifier>.Instance);

            var verdict = await verifier.VerifyAsync(HailMary(), "/tmp/book.mp3");

            Assert.NotEqual(VerificationOutcome.Match, verdict.Outcome);
            Assert.Equal("deterministic:whisper-base.en", verdict.Method);
        }

        [Fact]
        public async Task VerifyAsync_EscalationDisabled_NeverCallsEscalatedTranscription()
        {
            var whisper = new Mock<IWhisperService>();
            whisper.SetupGet(w => w.ModelName).Returns("base.en");
            whisper.Setup(w => w.TranscribeAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync("closing even by author c clock some story text follows here and continues on for a while longer");

            var samples = new AudioSampleSet { OpeningClipPath = "/tmp/opening.wav" };
            var extractor = new Mock<IAudioSampleExtractor>();
            extractor.Setup(e => e.ExtractAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<AudioSampleStrategy>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(samples);

            var configuration = new Mock<IConfigurationService>();
            configuration.Setup(c => c.GetApplicationSettingsAsync())
                .ReturnsAsync(new ApplicationSettings { VerificationEscalationModel = "" });

            var verifier = new DeterministicIdentityVerifier(
                extractor.Object, whisper.Object, configuration.Object,
                NullLogger<DeterministicIdentityVerifier>.Instance);

            await verifier.VerifyAsync(HailMary(), "/tmp/book.mp3");

            whisper.Verify(
                w => w.TranscribeWithModelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        [Fact]
        public async Task VerifyAsync_ImplausibleCompleteness_StillTranscribesTheClosingWindow()
        {
            // Live case ("Debt of Honor", an abridged cassette rip): perfect
            // opening credits produced a provisional Match, so the closing was
            // skipped and escalation never ran — and only THEN did the
            // completeness gate flag the book. The reviewer got an
            // opening-only transcript for a review-bound book whose closing
            // carries the deciding evidence ("…available on audio cassette…").
            // Completeness must fold in before those decisions.
            var whisper = new Mock<IWhisperService>();
            whisper.SetupGet(w => w.ModelName).Returns("base.en");
            whisper.Setup(w => w.TranscribeAsync("/tmp/opening.wav", It.IsAny<CancellationToken>()))
                .ReturnsAsync("Audible presents Project Hail Mary by Andy Weir narrated by Ray Porter");
            whisper.Setup(w => w.TranscribeAsync("/tmp/closing.wav", It.IsAny<CancellationToken>()))
                .ReturnsAsync("This concludes the abridged presentation of Project Hail Mary on audio cassette");

            var samples = new AudioSampleSet { OpeningClipPath = "/tmp/opening.wav", ClosingClipPath = "/tmp/closing.wav" };
            var extractor = new Mock<IAudioSampleExtractor>();
            extractor.Setup(e => e.ExtractAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<AudioSampleStrategy>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(samples);

            var configuration = new Mock<IConfigurationService>();
            configuration.Setup(c => c.GetApplicationSettingsAsync())
                .ReturnsAsync(new ApplicationSettings());

            var book = HailMary();
            book.Runtime = 960; // 16h catalog runtime…
            book.Files = new List<AudiobookFile>
            {
                new() { Path = "/x/part1.mp3", DurationSeconds = 3600, Size = 10_000_000 } // …but only 1h on disk
            };

            var verifier = new DeterministicIdentityVerifier(
                extractor.Object, whisper.Object, configuration.Object,
                NullLogger<DeterministicIdentityVerifier>.Instance);

            var verdict = await verifier.VerifyAsync(book, "/tmp/book.mp3");

            Assert.Equal(VerificationOutcome.Uncertain, verdict.Outcome);
            whisper.Verify(w => w.TranscribeAsync("/tmp/closing.wav", It.IsAny<CancellationToken>()), Times.Once);
            Assert.Contains("[closing]", verdict.Transcript);
        }

        [Fact]
        public async Task VerifyAsync_EscalatedClosingUnavailable_KeepsFirstPassClosingInTranscript()
        {
            // A null escalated window must not erase a good first-pass window:
            // the escalated verdict used to be built from the escalated texts
            // alone, dropping the base-tier closing from the stored transcript.
            var whisper = new Mock<IWhisperService>();
            whisper.SetupGet(w => w.ModelName).Returns("base.en");
            whisper.Setup(w => w.TranscribeAsync("/tmp/opening.wav", It.IsAny<CancellationToken>()))
                .ReturnsAsync("closing even by author c clock some story text follows here");
            whisper.Setup(w => w.TranscribeAsync("/tmp/closing.wav", It.IsAny<CancellationToken>()))
                .ReturnsAsync("read by ray porter for the unabridged edition");
            whisper.Setup(w => w.TranscribeWithModelAsync("/tmp/opening.wav", "small.en", It.IsAny<CancellationToken>()))
                .ReturnsAsync("Project Hail Mary by Andy Weir narrated by Ray Porter");
            whisper.Setup(w => w.TranscribeWithModelAsync("/tmp/closing.wav", "small.en", It.IsAny<CancellationToken>()))
                .ReturnsAsync((string?)null);

            var samples = new AudioSampleSet { OpeningClipPath = "/tmp/opening.wav", ClosingClipPath = "/tmp/closing.wav" };
            var extractor = new Mock<IAudioSampleExtractor>();
            extractor.Setup(e => e.ExtractAsync(It.IsAny<IReadOnlyList<string>>(), It.IsAny<IReadOnlyList<string>>(), It.IsAny<AudioSampleStrategy>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(samples);

            var configuration = new Mock<IConfigurationService>();
            configuration.Setup(c => c.GetApplicationSettingsAsync())
                .ReturnsAsync(new ApplicationSettings { VerificationEscalationModel = "small.en" });

            var verifier = new DeterministicIdentityVerifier(
                extractor.Object, whisper.Object, configuration.Object,
                NullLogger<DeterministicIdentityVerifier>.Instance);

            var verdict = await verifier.VerifyAsync(HailMary(), "/tmp/book.mp3");

            Assert.Equal("deterministic:whisper-base.en→small.en", verdict.Method);
            Assert.Contains("unabridged edition", verdict.Transcript);
        }
    }
}
