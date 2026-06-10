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
using Listenarr.Application.Search.Filters;
using Listenarr.Domain.Models;
using Xunit;

namespace Listenarr.Tests.Features.Api.Services
{
    [Trait("Name", "AudiobookOnlyFilterTests")]
    [Trait("Category", "Search")]
    public class AudiobookOnlyFilterTests
    {
        private static readonly AudiobookOnlyFilter Filter = new();

        [Fact]
        public void ShouldFilter_ComicCbr_MisMatchedToAudiobook_IsFiltered()
        {
            // The live loop: a .cbr comic whose title is relevant to the audiobook "Tom Corbett
            // Space Cadet" was grabbed (no comic-format entry in the denylist), then failed/import-
            // blocked and got re-grabbed. With no audio signal, the comic extension must filter it.
            var result = new SearchResult
            {
                Title = "Tom Corbett Space Cadet V2 001(2013)(Digital)(TLK EMPIRE HD).cbr ( Nem )",
            };

            Assert.True(Filter.ShouldFilter(result));
        }

        [Theory]
        [InlineData("Some Title.cbz ( group )")]
        [InlineData("Some Book.epub")]
        [InlineData("Series 01.mobi")]
        [InlineData("Movie Rip 2021.mkv")]
        [InlineData("Something.mp4")]
        public void ShouldFilter_NonAudioMediaExtensions_AreFiltered(string title)
        {
            Assert.True(Filter.ShouldFilter(new SearchResult { Title = title }));
        }

        [Fact]
        public void ShouldFilter_AudiobookWithBundledPdf_IsNotFiltered()
        {
            // False-positive guard: a real audiobook release that ships a companion PDF must NOT be
            // filtered. Bare "PDF" is deliberately excluded from the denylist, and .m4b/.mp3 never
            // collide with the comic/ebook/video extensions.
            var result = new SearchResult
            {
                Title = "John Dies at the End (Unabridged) [M4B + PDF]",
            };

            Assert.False(Filter.ShouldFilter(result));
        }

        [Fact]
        public void ShouldFilter_PlainAudiobookRelease_IsNotFiltered()
        {
            var result = new SearchResult
            {
                Title = "David Copperfield - Charles Dickens (Unabridged) [MP3]",
            };

            Assert.False(Filter.ShouldFilter(result));
        }

        [Theory]
        [InlineData("Calhem Parker and Ola Rostrom-Clash of the Titans-[IVT075]-SINGLE-WEB-2026-PTC")]
        [InlineData("Some Artist - Some Track-EP-WEB-2024-GRP")]
        [InlineData("Various - Some Comp-ALBUM-CD-2019-GRP")]
        [InlineData("Artist - Title (CDM-FLAC-2020-XYZ)")]
        public void ShouldFilter_MusicSceneRelease_MisMatchedToAudiobook_IsFiltered(string title)
        {
            // The live case: a techno single "...Clash of the Titans-[IVT075]-SINGLE-WEB-2026-PTC"
            // matched the generic audiobook "Titans" by title, passed the filter (music files are
            // .mp3/.flac, same as audiobooks), was grabbed and imported as the wrong content. With no
            // audio signal, the scene format-pair (SINGLE-WEB / EP-WEB / ALBUM-CD / CDM-FLAC) filters it.
            Assert.True(Filter.ShouldFilter(new SearchResult { Title = title }));
        }

        [Theory]
        [InlineData("Brandon Sanderson - The Hero of Ages (Mistborn #3) [Graphic Audio]")]
        [InlineData("James Patterson - Michael Bennett 17 - Paranoia.m4b")]
        [InlineData("Starsight, Skyward (02) by Brandon Sanderson M4B")]
        [InlineData("Roald Dahl - Boy - Going Solo - BBC Audio Drama")]
        [InlineData("Ann Cleeves - Single Malt Murder (Unabridged) [MP3]")]
        [InlineData("The Outcast - Some Story - WEB Edition 2024")]
        public void ShouldFilter_RealAudiobook_NotMisreadAsMusic_IsNotFiltered(string title)
        {
            // False-positive guard against the music heuristic. None of these is a music-scene
            // format-pair: "Single Malt" / a bare "WEB Edition" have no release-type+medium pairing,
            // and the M4B/MP3 titles are plain audiobooks. The pattern requires the *delimited pair*
            // (SINGLE-WEB, EP-CD, …), so generic titles and web rips are not caught.
            Assert.False(Filter.ShouldFilter(new SearchResult { Title = title }));
        }

        [Fact]
        public void ShouldFilter_NonAudioExtension_ButHasAudioRuntime_IsNotFiltered()
        {
            // Positive audio signal wins: if a result carries an explicit runtime it's treated as
            // audio and returns early, before the format denylist runs. Documents the boundary.
            var result = new SearchResult
            {
                Title = "Some Audiobook.mp4",
                Runtime = 480,
            };

            Assert.False(Filter.ShouldFilter(result));
        }
    }
}
