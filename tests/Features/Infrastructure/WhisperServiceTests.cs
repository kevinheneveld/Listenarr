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
using Listenarr.Infrastructure.Whisper;
using Xunit;

namespace Listenarr.Tests.Features.Infrastructure
{
    public class WhisperServiceTests
    {
        [Fact]
        public void NormalizeTranscriptOutput_StripsSegmentTimestamps_AndJoinsSegments()
        {
            // Real whisper-cli timestamped output shape (the production decode runs
            // WITH timestamps because suppressing them makes base.en skip
            // music-overlaid segments such as the spoken credits).
            var stdout =
                "[00:00:00.000 --> 00:00:09.260]   \"This is Audible,\" A Court of Thorns and Roses,\n" +
                "[00:00:09.260 --> 00:00:17.160]   by Sarah J. Mass, narrated by Jennifer Eketa.\n" +
                "[00:00:17.160 --> 00:00:23.520]   Chapter 1 The forest had become a labyrinth of snow\n";

            var transcript = WhisperService.NormalizeTranscriptOutput(stdout);

            Assert.Equal(
                "\"This is Audible,\" A Court of Thorns and Roses, by Sarah J. Mass, narrated by Jennifer Eketa. Chapter 1 The forest had become a labyrinth of snow",
                transcript);
        }

        [Fact]
        public void NormalizeTranscriptOutput_PlainTextWithoutTimestamps_PassesThrough()
        {
            var transcript = WhisperService.NormalizeTranscriptOutput("  Plain text output.  \n");
            Assert.Equal("Plain text output.", transcript);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   \n  \n")]
        public void NormalizeTranscriptOutput_EmptyOutput_ReturnsNull(string? stdout)
        {
            Assert.Null(WhisperService.NormalizeTranscriptOutput(stdout));
        }
    }
}
