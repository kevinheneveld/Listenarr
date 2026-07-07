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
using Listenarr.Application.Audiobooks.Series;

namespace Listenarr.Tests.Features.Application.Audiobooks
{
    [Trait("Area", "Series")]
    [Trait("Name", "SeriesWorkKeyTests")]
    public class SeriesWorkKeyTests
    {
        [Fact]
        public void MultipleRecordings_SamePosition_AreOneWork()
        {
            // Three recordings of Reacher book 1: plain, narrator re-release, subtitled edition.
            var a = SeriesWorkKey.Build("Killing Floor", new[] { "Lee Child" }, "1");
            var b = SeriesWorkKey.Build("Killing Floor: Jack Reacher, Book 1", new[] { "Lee Child" }, "1.0");
            var c = SeriesWorkKey.Build("Killing Floor", new[] { "Lee Child" }, "01");

            Assert.Equal(a, b);
            Assert.Equal(a, c);
        }

        [Fact]
        public void PositionlessEntries_KeyByNormalizedTitle()
        {
            var a = SeriesWorkKey.Build("Before Eden", new[] { "Arthur C. Clarke" }, null);
            var b = SeriesWorkKey.Build("Before Eden!", new[] { "Arthur C. Clarke" }, "");

            Assert.Equal(a, b);
            Assert.DoesNotContain("#", a);
        }

        [Fact]
        public void VolumeNumberedDistinctWorks_StayDistinct()
        {
            // The kevin/live original bug: trailing-number strip must not merge
            // real volumes — position keeps them apart.
            var v1 = SeriesWorkKey.Build("The Sharing Knife, Volume 1", new[] { "Lois McMaster Bujold" }, "1");
            var v2 = SeriesWorkKey.Build("The Sharing Knife, Volume 2", new[] { "Lois McMaster Bujold" }, "2");

            Assert.NotEqual(v1, v2);
        }

        [Fact]
        public void DifferentPositions_NeverMerge_EvenWithSameTitle()
        {
            var p1 = SeriesWorkKey.Build("Collected Stories", new[] { "Author" }, "1");
            var p2 = SeriesWorkKey.Build("Collected Stories", new[] { "Author" }, "2");

            Assert.NotEqual(p1, p2);
        }

        [Fact]
        public void AuthorPrefix_AndSubtitle_AreStripped()
        {
            var plain = SeriesWorkKey.Build("Friday", new[] { "Robert A. Heinlein" }, null);
            var branded = SeriesWorkKey.Build("Robert A. Heinlein Friday: A Novel", new[] { "Robert A. Heinlein" }, null);

            Assert.Equal(plain, branded);
        }

        [Fact]
        public void FractionalPositions_Canonicalize()
        {
            Assert.Equal(SeriesWorkKey.NormalizePosition("4.5"), SeriesWorkKey.NormalizePosition("4.50"));
            Assert.Equal("1", SeriesWorkKey.NormalizePosition("1.0"));
            Assert.Equal("novella", SeriesWorkKey.NormalizePosition("Novella"));
        }
    }
}
