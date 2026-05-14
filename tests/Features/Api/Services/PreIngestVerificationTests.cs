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
using Listenarr.Api.Services;
using Listenarr.Domain.Models;

namespace Listenarr.Tests.Features.Api.Services
{
    public class PreIngestVerificationTests
    {
        private static List<AudioMetadata> Files(int count, double durationSeconds, string? language = null)
        {
            var list = new List<AudioMetadata>();
            for (var i = 0; i < count; i++)
            {
                list.Add(new AudioMetadata
                {
                    Duration = TimeSpan.FromSeconds(durationSeconds),
                    Language = language,
                });
            }
            return list;
        }

        private static readonly List<string> EnglishOnly = new() { "English" };

        // ── music-shape detection ────────────────────────────────────────────

        [Fact]
        public void Inspect_RejectsManyShortTracks_AsMusicAlbum()
        {
            // 12 files, ~3.3 min each — classic music album shape.
            var result = PreIngestVerification.Inspect(Files(12, 200), EnglishOnly);
            Assert.True(result.Rejected);
            Assert.Contains("music album", result.Reason);
        }

        [Fact]
        public void Inspect_KeepsManyLongChapters_AsAudiobook()
        {
            // 12 files, 1 hour each — long-form audiobook chapters.
            var result = PreIngestVerification.Inspect(Files(12, 3600), EnglishOnly);
            Assert.False(result.Rejected);
        }

        [Fact]
        public void Inspect_KeepsShortBatchBelowFileCountThreshold()
        {
            // Only 5 short files — too few to call it a music album.
            var result = PreIngestVerification.Inspect(Files(5, 200), EnglishOnly);
            Assert.False(result.Rejected);
        }

        [Fact]
        public void Inspect_FailsOpenWhenNoDurationSignal()
        {
            // 12 files but ffprobe gave no usable durations — don't guess.
            var result = PreIngestVerification.Inspect(Files(12, 0), EnglishOnly);
            Assert.False(result.Rejected);
        }

        [Fact]
        public void Inspect_KeepsManyMediumTracksAtThresholdBoundary()
        {
            // Median exactly at the 300s threshold is not below it — keep.
            var result = PreIngestVerification.Inspect(Files(12, 300), EnglishOnly);
            Assert.False(result.Rejected);
        }

        // ── language tag check ───────────────────────────────────────────────

        [Fact]
        public void Inspect_RejectsForeignLanguageTag()
        {
            var files = Files(3, 3600, language: "ger");
            var result = PreIngestVerification.Inspect(files, EnglishOnly);
            Assert.True(result.Rejected);
            Assert.Contains("German", result.Reason);
        }

        [Fact]
        public void Inspect_KeepsMatchingLanguageTag()
        {
            var files = Files(3, 3600, language: "eng");
            var result = PreIngestVerification.Inspect(files, EnglishOnly);
            Assert.False(result.Rejected);
        }

        [Fact]
        public void Inspect_FailsOpenWhenLanguageTagMissing()
        {
            // The common case: audiobook rips carry no language tag at all.
            var files = Files(3, 3600, language: null);
            Assert.False(PreIngestVerification.Inspect(files, EnglishOnly).Rejected);
        }

        [Fact]
        public void Inspect_FailsOpenWhenLanguageTagUnrecognised()
        {
            var files = Files(3, 3600, language: "zxx");
            Assert.False(PreIngestVerification.Inspect(files, EnglishOnly).Rejected);
        }

        [Fact]
        public void Inspect_NoPreferredLanguages_KeepsForeignTag()
        {
            var files = Files(3, 3600, language: "ger");
            Assert.False(PreIngestVerification.Inspect(files, null).Rejected);
            Assert.False(PreIngestVerification.Inspect(files, new List<string>()).Rejected);
        }

        [Fact]
        public void Inspect_NonEnglishProfile_KeepsThatLanguageTag()
        {
            var files = Files(3, 3600, language: "ger");
            var result = PreIngestVerification.Inspect(files, new List<string> { "German" });
            Assert.False(result.Rejected);
        }

        [Fact]
        public void Inspect_EmptyBatch_IsAccepted()
        {
            var result = PreIngestVerification.Inspect(new List<AudioMetadata>(), EnglishOnly);
            Assert.False(result.Rejected);
        }
    }
}
