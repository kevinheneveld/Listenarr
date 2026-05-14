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
using Xunit;
using Moq;
using Listenarr.Api.Services;
using Listenarr.Api.Services.Scoring;
using Listenarr.Application.Repositories;
using Listenarr.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Api.Services
{
    public class SearchRelevanceFilterTests
    {
        // ── Pure-function relevance ratio tests ──────────────────────────────

        [Fact]
        public void Relevance_RejectsMusicAlbumThatSharesOneAuthorSurname()
        {
            // The exact false-match from the bug report: a Patterson Hood concert
            // recording surfaced for a James Patterson audiobook query.
            var relevance = RelevanceFilter.ComputeRelevance(
                resultTitle: "Patterson Hood Solo Acoustic The Hargray Capitol Theatre Macon, Georgia 12/3/2023",
                audiobookTitle: "Dog Diaries: Ruffing It",
                authors: new[] { "James Patterson" });
            Assert.True(relevance < RelevanceFilter.DefaultMinRelevance,
                $"Expected music-album match to be below {RelevanceFilter.DefaultMinRelevance:P0} relevance, got {relevance:P0}");
        }

        [Fact]
        public void Relevance_AcceptsAuthorFirstFormat()
        {
            // Common torrent naming: "Author Name - Book Title (year)"
            var relevance = RelevanceFilter.ComputeRelevance(
                resultTitle: "Roald Dahl - The Wonderful Story of Henry Sugar [m4b]",
                audiobookTitle: "The Wonderful Story of Henry Sugar",
                authors: new[] { "Roald Dahl" });
            Assert.True(relevance >= RelevanceFilter.DefaultMinRelevance,
                $"Expected legitimate match to clear {RelevanceFilter.DefaultMinRelevance:P0} relevance, got {relevance:P0}");
        }

        [Fact]
        public void Relevance_AcceptsTitleFirstFormat()
        {
            var relevance = RelevanceFilter.ComputeRelevance(
                resultTitle: "David Copperfield [Audiobook] by Charles Dickens, narrated by Richard Armitage",
                audiobookTitle: "David Copperfield",
                authors: new[] { "Charles Dickens" });
            Assert.True(relevance >= RelevanceFilter.DefaultMinRelevance,
                $"Expected legitimate match to clear {RelevanceFilter.DefaultMinRelevance:P0} relevance, got {relevance:P0}");
        }

        [Fact]
        public void Relevance_RejectsUnrelatedTitleEvenWithCommonStopWords()
        {
            // Both contain "the" / "of" but no shared significant tokens.
            var relevance = RelevanceFilter.ComputeRelevance(
                resultTitle: "The Grateful Dead - Live at the Capitol Theatre 1971",
                audiobookTitle: "The Wonderful Story of Henry Sugar",
                authors: new[] { "Roald Dahl" });
            Assert.True(relevance < RelevanceFilter.DefaultMinRelevance,
                $"Expected unrelated title to fall below {RelevanceFilter.DefaultMinRelevance:P0} relevance, got {relevance:P0}");
        }

        [Fact]
        public void Relevance_FailsOpenWhenAudiobookHasNoSignificantTokens()
        {
            // If the audiobook side has no significant tokens to match against,
            // the filter shouldn't reject everything — it can't judge.
            var relevance = RelevanceFilter.ComputeRelevance(
                resultTitle: "Some Random Torrent Title",
                audiobookTitle: "It",
                authors: null);
            Assert.Equal(1.0, relevance);
        }

        [Fact]
        public void Relevance_IsCaseAndPunctuationInsensitive()
        {
            var relevance = RelevanceFilter.ComputeRelevance(
                resultTitle: "DICKENS, charles -- david-copperfield.m4b",
                audiobookTitle: "David Copperfield",
                authors: new[] { "Charles Dickens" });
            Assert.True(relevance >= RelevanceFilter.DefaultMinRelevance);
        }

        // ── Scorer integration tests ─────────────────────────────────────────

        private static SearchResultScorer CreateScorer()
        {
            return new SearchResultScorer(
                Mock.Of<IIndexerRepository>(),
                NullLogger.Instance);
        }

        private static QualityProfile PermissiveProfile() => new()
        {
            MinimumSize = 0,
            MaximumSize = 0,
            MinimumSeeders = 0,
            MaximumAge = 0,
            MinimumScore = 0,
            MustContain = new List<string>(),
            MustNotContain = new List<string>(),
            PreferredFormats = new List<string>(),
            PreferredWords = new List<string>(),
            PreferredLanguages = new List<string>(),
            Qualities = new List<QualityDefinition>(),
        };

        [Fact]
        public async Task Scorer_RejectsResultWhenAudiobookSuppliedAndTitleNotRelevant()
        {
            var scorer = CreateScorer();
            var profile = PermissiveProfile();

            var audiobook = new Audiobook
            {
                Title = "Dog Diaries: Ruffing It",
                Authors = new List<string> { "James Patterson" },
            };
            var result = new SearchResult
            {
                Title = "Patterson Hood Solo Acoustic The Hargray Capitol Theatre Macon, Georgia 12/3/2023",
                Size = 459 * 1024 * 1024,
                Seeders = 1,
                DownloadType = "torrent",
            };

            var score = await scorer.Score(result, profile, audiobook);
            Assert.True(score.IsRejected,
                $"Expected music-album result to be rejected, but it scored {score.TotalScore} with reasons: [{string.Join("; ", score.RejectionReasons)}]");
            Assert.Contains(score.RejectionReasons, r => r.Contains("relevant", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task Scorer_DoesNotApplyRelevanceCheckWhenAudiobookOmitted()
        {
            // Manual search keeps the old behaviour: the scorer doesn't have
            // the audiobook context and shouldn't reject for low relevance.
            var scorer = CreateScorer();
            var profile = PermissiveProfile();

            var result = new SearchResult
            {
                Title = "Patterson Hood Solo Acoustic The Hargray Capitol Theatre",
                Size = 459 * 1024 * 1024,
                Seeders = 1,
                DownloadType = "torrent",
            };

            var score = await scorer.Score(result, profile, audiobook: null);
            Assert.DoesNotContain(score.RejectionReasons, r => r.Contains("relevant", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public async Task Scorer_AcceptsLegitimateAudiobookMatch()
        {
            var scorer = CreateScorer();
            var profile = PermissiveProfile();

            var audiobook = new Audiobook
            {
                Title = "David Copperfield",
                Authors = new List<string> { "Charles Dickens" },
            };
            var result = new SearchResult
            {
                Title = "David Copperfield [Audiobook] by Charles Dickens, narrated by Richard Armitage",
                Size = 800 * 1024 * 1024,
                Seeders = 25,
                Format = "m4b",
                Language = "English",
                DownloadType = "torrent",
            };

            var score = await scorer.Score(result, profile, audiobook);
            Assert.False(score.IsRejected,
                $"Expected legitimate match to be accepted, but got rejection reasons: [{string.Join("; ", score.RejectionReasons)}]");
            Assert.True(score.ScoreBreakdown.ContainsKey("RelevancePercent"),
                "Expected RelevancePercent to be recorded in score breakdown");
        }
    }
}
