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
using Listenarr.Infrastructure.DownloadClients.Qbittorrent;

namespace Listenarr.Tests.Features.Infrastructure.DownloadClients.Qbittorrent
{
    [Trait("Area", "DownloadClients")]
    [Trait("Name", "QbittorrentCompletionCandidateTests")]
    public class QbittorrentCompletionCandidateTests
    {
        [Fact]
        public void MetaDlWithZeroAmountLeft_IsNotComplete()
        {
            // The live bug: a dead magnet fetching metadata reports
            // AmountLeft == 0 (size unknown) at 0% progress and was marked
            // Complete, spawning import jobs that ground forever.
            Assert.False(QBittorrentHelpers.IsCompleteCandidate(
                progress: 0.0, amountLeft: 0, size: 0, state: "metaDL"));
        }

        [Fact]
        public void MetaDlEvenWithReportedSize_IsNotComplete()
        {
            Assert.False(QBittorrentHelpers.IsCompleteCandidate(
                progress: 0.0, amountLeft: 0, size: 1000, state: "metaDL"));
        }

        [Fact]
        public void ZeroSize_IsNeverComplete_RegardlessOfState()
        {
            Assert.False(QBittorrentHelpers.IsCompleteCandidate(
                progress: 1.0, amountLeft: 0, size: 0, state: "stalledUP"));
        }

        [Theory]
        [InlineData("uploading")]
        [InlineData("stalledUP")]
        [InlineData("stoppedUP")]
        [InlineData("pausedUP")]
        public void FinishedTorrentWithContent_IsComplete(string state)
        {
            Assert.True(QBittorrentHelpers.IsCompleteCandidate(
                progress: 1.0, amountLeft: 0, size: 500_000_000, state: state));
        }

        [Fact]
        public void CheckingResumeData_IsNotComplete()
        {
            // Post-restart recheck: AmountLeft can read 0 before verification
            // finishes — not yet proof of completion.
            Assert.False(QBittorrentHelpers.IsCompleteCandidate(
                progress: 0.0, amountLeft: 0, size: 500_000_000, state: "checkingResumeData"));
        }

        [Fact]
        public void PartialDownload_IsNotComplete()
        {
            Assert.False(QBittorrentHelpers.IsCompleteCandidate(
                progress: 0.4, amountLeft: 300_000_000, size: 500_000_000, state: "downloading"));
        }

        [Fact]
        public void CompletedByAmountLeft_WithRealContent_IsComplete()
        {
            // Progress can report fractionally below 1.0 on some clients while
            // AmountLeft has hit zero — with real content that IS completion.
            Assert.True(QBittorrentHelpers.IsCompleteCandidate(
                progress: 0.9999, amountLeft: 0, size: 500_000_000, state: "uploading"));
        }
    }
}
