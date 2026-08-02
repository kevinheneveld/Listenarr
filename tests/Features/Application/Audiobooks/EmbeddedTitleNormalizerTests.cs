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
    [Trait("Area", "Application")]
    [Trait("Name", "EmbeddedTitleNormalizerTests")]
    public class EmbeddedTitleNormalizerTests
    {
        [Theory]
        [InlineData("Ch75 - The Hard Way", "The Hard Way")]           // the live 77-group case
        [InlineData("Chapter 12: The Hard Way", "The Hard Way")]
        [InlineData("Track 03 The Hard Way", "The Hard Way")]
        [InlineData("The Hard Way - Chapter 12", "The Hard Way")]
        [InlineData("The Hard Way (Part 2)", "The Hard Way")]
        [InlineData("The Hard Way, Disc 3", "The Hard Way")]
        public void ChapterMarkers_AreStripped(string tagged, string expected)
        {
            Assert.Equal(expected, EmbeddedTitleNormalizer.StripChapterMarkers(tagged));
        }

        [Theory]
        [InlineData("Chapter 12")]
        [InlineData("Track 3 of 20")]
        [InlineData("Pt. 4")]
        [InlineData("   ")]
        [InlineData(null)]
        public void MarkerOnlyOrEmptyTags_CarryNoBookIdentity(string? tagged)
        {
            Assert.Null(EmbeddedTitleNormalizer.StripChapterMarkers(tagged));
        }

        [Theory]
        [InlineData("The Hard Way")]                                   // untouched
        [InlineData("Catch-22")]                                       // hyphen-number is the TITLE
        [InlineData("Fahrenheit 451")]                                 // trailing number without marker word
        [InlineData("1984")]
        public void RealTitles_SurviveUnchanged(string title)
        {
            Assert.Equal(title, EmbeddedTitleNormalizer.StripChapterMarkers(title));
        }

        [Fact]
        public void PartOfSeriesTitle_KeepsBookIdentity()
        {
            // Only ONE trailing marker strips — "The Dark Tower Part 2" is a
            // plausible real title shape, but paired with a leading chapter it
            // is clearly per-file tagging.
            Assert.Equal("The Gunslinger", EmbeddedTitleNormalizer.StripChapterMarkers("Ch3 - The Gunslinger"));
        }
    }
}
