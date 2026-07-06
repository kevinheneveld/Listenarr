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

namespace Listenarr.Tests.Features.Application.Audiobooks
{
    [Trait("Area", "Verification")]
    [Trait("Name", "WrongContentAutoRejectTests")]
    public class WrongContentAutoRejectTests
    {
        private static bool Decide(
            bool enabled = true,
            string? trigger = VerificationTriggers.Import,
            VerificationOutcome outcome = VerificationOutcome.Mismatch,
            double? confidence = 0.95,
            string? heardTitle = "Some Other Book",
            string? heardAuthor = null)
            => WrongContentAutoReject.ShouldAutoReject(enabled, trigger, outcome, confidence, heardTitle, heardAuthor);

        [Fact]
        public void Rejects_ConfidentCreditBackedMismatch_OnImport()
        {
            Assert.True(Decide());
            Assert.True(Decide(heardTitle: null, heardAuthor: "Patterson Hood"));
        }

        [Fact]
        public void NeverRejects_WhenSettingDisabled()
        {
            Assert.False(Decide(enabled: false));
        }

        [Theory]
        [InlineData(VerificationTriggers.Import)]
        [InlineData(VerificationTriggers.Manual)]
        public void Rejects_OnQualifyingTriggers(string trigger)
        {
            // Manual covers the batch endpoints too (POST /verification/batch and
            // the dashboard Re-check enqueue use the default Manual trigger).
            Assert.True(Decide(trigger: trigger));
        }

        [Theory]
        [InlineData(VerificationTriggers.Transfer)]
        [InlineData(VerificationTriggers.Metadata)]
        [InlineData("someday-new-trigger")]
        [InlineData(null)]
        public void NeverRejects_OnNonQualifyingTriggers(string? trigger)
        {
            Assert.False(Decide(trigger: trigger));
        }

        [Theory]
        [InlineData(VerificationOutcome.Uncertain)]
        [InlineData(VerificationOutcome.Match)]
        [InlineData(VerificationOutcome.NoSpokenCredits)]
        public void NeverRejects_NonMismatchOutcomes(VerificationOutcome outcome)
        {
            Assert.False(Decide(outcome: outcome));
        }

        [Fact]
        public void NeverRejects_BelowConfidenceFloor()
        {
            Assert.False(Decide(confidence: 0.89));
            Assert.False(Decide(confidence: null));
            Assert.True(Decide(confidence: WrongContentAutoReject.MinConfidence));
        }

        [Fact]
        public void NeverRejects_WithoutCreditEvidence()
        {
            // A Mismatch with no heard title AND no heard author is absence-of-match,
            // not evidence of wrong content — a human's call.
            Assert.False(Decide(heardTitle: null, heardAuthor: null));
            Assert.False(Decide(heardTitle: "  ", heardAuthor: ""));
        }

        [Fact]
        public void TriggerComparison_IsCaseInsensitive()
        {
            Assert.True(Decide(trigger: "Import"));
            Assert.True(Decide(trigger: "IMPORT"));
        }
    }
}
