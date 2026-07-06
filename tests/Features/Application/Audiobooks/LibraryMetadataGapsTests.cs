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
using Listenarr.Application.Audiobooks.Catalog;

namespace Listenarr.Tests.Features.Application.Audiobooks
{
    [Trait("Area", "Dashboard")]
    [Trait("Name", "LibraryMetadataGapsTests")]
    public class LibraryMetadataGapsTests
    {
        [Fact]
        public void CoverAndDescription_MissingWhenBlank()
        {
            var b = new Audiobook { ImageUrl = " ", Description = null };
            Assert.True(LibraryMetadataGaps.MissesField(b, MissingField.CoverArt));
            Assert.True(LibraryMetadataGaps.MissesField(b, MissingField.Description));

            b.ImageUrl = "/cache/images/library/x.jpg";
            b.Description = "A book.";
            Assert.False(LibraryMetadataGaps.MissesField(b, MissingField.CoverArt));
            Assert.False(LibraryMetadataGaps.MissesField(b, MissingField.Description));
        }

        [Fact]
        public void Narrators_MissingWhenNullEmptyOrWhitespaceOnly()
        {
            Assert.True(LibraryMetadataGaps.MissesField(new Audiobook { Narrators = null }, MissingField.Narrators));
            Assert.True(LibraryMetadataGaps.MissesField(new Audiobook { Narrators = new List<string>() }, MissingField.Narrators));
            Assert.True(LibraryMetadataGaps.MissesField(new Audiobook { Narrators = new List<string> { " " } }, MissingField.Narrators));
            Assert.False(LibraryMetadataGaps.MissesField(new Audiobook { Narrators = new List<string> { "Ray Porter" } }, MissingField.Narrators));
        }

        [Fact]
        public void SeriesPosition_OnlyAppliesToSeriesBooks()
        {
            // No series at all: not a gap.
            Assert.False(LibraryMetadataGaps.MissesField(new Audiobook(), MissingField.SeriesPosition));

            // Series name without a number: gap.
            var inSeries = new Audiobook { Series = "The Cosmere" };
            Assert.True(LibraryMetadataGaps.MissesField(inSeries, MissingField.SeriesPosition));

            // Legacy number fills the gap.
            inSeries.SeriesNumber = "2";
            Assert.False(LibraryMetadataGaps.MissesField(inSeries, MissingField.SeriesPosition));

            // Membership takes precedence over legacy fields.
            var withMembership = new Audiobook
            {
                SeriesMemberships = new List<AudiobookSeriesMembership>
                {
                    new() { SeriesName = "The Dark", IsPrimary = true, SeriesNumber = null },
                },
            };
            Assert.True(LibraryMetadataGaps.MissesField(withMembership, MissingField.SeriesPosition));
            withMembership.SeriesMemberships[0].SeriesNumber = "5";
            Assert.False(LibraryMetadataGaps.MissesField(withMembership, MissingField.SeriesPosition));
        }
    }
}
