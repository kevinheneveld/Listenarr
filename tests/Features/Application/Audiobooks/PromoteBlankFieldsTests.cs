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
using Listenarr.Application.Audiobooks.Files;

namespace Listenarr.Tests.Features.Application.Audiobooks
{
    /// <summary>
    /// The "Fill missing from file" backfill must only ever FILL blanks — an
    /// existing library value always wins over what the file's tags claim.
    /// </summary>
    public class PromoteBlankFieldsTests
    {
        [Fact]
        public void FillsBlankFields_FromFileTags()
        {
            var audiobook = new Audiobook { Id = 1 };
            var meta = new AudioMetadata
            {
                Title = "Tagged Title",
                Artist = "Tagged Author",
                Narrator = "Tagged Narrator",
                Publisher = "Tagged House",
                Series = "Tagged Series",
                SeriesPosition = 2,
                Year = 2020,
            };

            var changed = AudiobookFileService.PromoteBlankFieldsFromMetadata(audiobook, meta, out _);

            Assert.True(changed);
            Assert.Equal("Tagged Title", audiobook.Title);
            Assert.Equal(new List<string> { "Tagged Author" }, audiobook.Authors);
            Assert.Equal(new List<string> { "Tagged Narrator" }, audiobook.Narrators);
            Assert.Equal("Tagged House", audiobook.Publisher);
            Assert.Equal("Tagged Series", audiobook.Series);
            Assert.Equal("2", audiobook.SeriesNumber);
            Assert.Equal("2020", audiobook.PublishYear);
        }

        [Fact]
        public void NeverOverwrites_ExistingValues()
        {
            var audiobook = new Audiobook
            {
                Id = 1,
                Title = "Library Title",
                Authors = new List<string> { "Library Author" },
                Publisher = "Library House",
            };
            var meta = new AudioMetadata { Title = "Tagged Title", Artist = "Tagged Author", Publisher = "Tagged House" };

            var changed = AudiobookFileService.PromoteBlankFieldsFromMetadata(audiobook, meta, out _);

            Assert.False(changed);
            Assert.Equal("Library Title", audiobook.Title);
            Assert.Equal(new List<string> { "Library Author" }, audiobook.Authors);
            Assert.Equal("Library House", audiobook.Publisher);
        }

        [Fact]
        public void AsinFromTags_FlagsIdentifierChange()
        {
            var audiobook = new Audiobook { Id = 1, Title = "T" };
            var meta = new AudioMetadata { Asin = "B0ABCDEFGH" };

            var changed = AudiobookFileService.PromoteBlankFieldsFromMetadata(audiobook, meta, out var identifiersChanged);

            Assert.True(changed);
            Assert.True(identifiersChanged);
            Assert.Equal("B0ABCDEFGH", audiobook.Asin);
        }

        [Fact]
        public void AuthorCandidateMatchingNarrator_IsNotPromoted()
        {
            // A tag set where artist == narrator is a narrator credit, not an author.
            var audiobook = new Audiobook { Id = 1, Title = "T" };
            var meta = new AudioMetadata { Artist = "Ray Porter", Narrator = "Ray Porter" };

            AudiobookFileService.PromoteBlankFieldsFromMetadata(audiobook, meta, out _);

            Assert.True(audiobook.Authors == null || audiobook.Authors.Count == 0);
            Assert.Equal(new List<string> { "Ray Porter" }, audiobook.Narrators);
        }
    }
}
