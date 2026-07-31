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
using Listenarr.Application.Audiobooks.Renaming;

namespace Listenarr.Tests.Features.Application.Audiobooks
{
    [Trait("Area", "Application")]
    [Trait("Name", "AudiobookTitleFoldingTests")]
    public class AudiobookTitleFoldingTests
    {
        [Fact]
        public void VolumeSubtitle_FoldsIntoTitle()
        {
            // Audible hides some volume numbers in the subtitle; without
            // folding, volumes 3 and 8 collide into one folder.
            Assert.Equal(
                "Favorite Science Fiction Stories: Volume 3",
                AudiobookTitleFolding.CombinedTitle(
                    "Favorite Science Fiction Stories", "Volume 3",
                    "Favorite Science Fiction Stories", patternsUseSubtitleToken: false));
        }

        [Fact]
        public void SeriesEchoSubtitle_IsNotFolded()
        {
            // Live case: subtitle "The Chronicles of Narnia" under series
            // "The Chronicles of Narnia (Publication Order)" produced
            // ".../The Chronicles of Narnia (Publication Order)/Prince
            // Caspian - The Chronicles of Narnia/" — the series folder
            // already says it.
            Assert.Equal(
                "Prince Caspian",
                AudiobookTitleFolding.CombinedTitle(
                    "Prince Caspian", "The Chronicles of Narnia",
                    "The Chronicles of Narnia (Publication Order)", patternsUseSubtitleToken: false));
        }

        [Fact]
        public void ExplicitSubtitleToken_AlwaysPlainTitle()
        {
            Assert.Equal(
                "Favorite Science Fiction Stories",
                AudiobookTitleFolding.CombinedTitle(
                    "Favorite Science Fiction Stories", "Volume 3",
                    "Favorite Science Fiction Stories", patternsUseSubtitleToken: true));
        }

        [Fact]
        public void SubtitleAlreadyInTitle_NotDuplicated()
        {
            Assert.Equal(
                "Dune: Deluxe Edition",
                AudiobookTitleFolding.CombinedTitle(
                    "Dune: Deluxe Edition", "Deluxe Edition", null, patternsUseSubtitleToken: false));
        }

        [Fact]
        public void NoSubtitle_PlainTitle()
        {
            Assert.Equal(
                "The Horse and His Boy",
                AudiobookTitleFolding.CombinedTitle(
                    "The Horse and His Boy", null, "The Chronicles of Narnia", patternsUseSubtitleToken: false));
        }

        [Theory]
        [InlineData("The Chronicles of Narnia", "The Chronicles of Narnia (Publication Order)", true)]
        [InlineData("The Chronicles of Narnia (Publication Order)", "The Chronicles of Narnia", true)]
        [InlineData("Volume 3", "Favorite Science Fiction Stories", false)]
        [InlineData(null, "Some Series", false)]
        [InlineData("A Subtitle", null, false)]
        public void SubtitleEchoesSeries_Cases(string? subtitle, string? series, bool expected)
        {
            Assert.Equal(expected, AudiobookTitleFolding.SubtitleEchoesSeries(subtitle, series));
        }
    }
}
