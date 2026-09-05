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

namespace Listenarr.Tests.Features.Application.Search
{
    /// <summary>
    /// The anti-junk guards: music-scene release grammar and non-audio media
    /// formats must filter when there is no positive audio evidence — the live
    /// failure was music singles being grabbed in place of audiobooks.
    /// </summary>
    public class AudiobookOnlyFilter_MusicSceneTests
    {
        private static SearchResult Result(string title, string? format = null) => new()
        {
            Title = title,
            Format = format ?? string.Empty,
            Size = 100 * 1024 * 1024,
            DownloadType = "torrent"
        };

        [Fact]
        public void UndottedComicRelease_IsFiltered()
        {
            // Live case: indexer stripped the dot, so ".cbr" never matched and this comic was
            // grabbed for the audiobook "The Iron Maiden" and re-picked for weeks.
            var filter = new AudiobookOnlyFilter();
            Assert.True(filter.ShouldFilter(Result("Robyn Hood Iron Maiden 02 (of 02) (2021) (digital) (The Seeker Empire) cbr")));
        }

        [Fact]
        public void ComicIssueGrammarWithoutExtension_IsFiltered()
        {
            var filter = new AudiobookOnlyFilter();
            Assert.True(filter.ShouldFilter(Result("Tom Corbett Space Cadet V2 001 (2013) (Digital) (TLK-EMPIRE-HD)")));
            Assert.True(filter.ShouldFilter(Result("Saga 03 (of 12) (2022) (Digital-HD)")));
        }

        [Fact]
        public void BareEbookTokens_AreFiltered_ButAudioBundlesAreNot()
        {
            var filter = new AudiobookOnlyFilter();
            Assert.True(filter.ShouldFilter(Result("Project Hail Mary by Andy Weir epub")));
            Assert.True(filter.ShouldFilter(Result("Project Hail Mary (2021) mobi azw3")));
            Assert.False(filter.ShouldFilter(Result("Project Hail Mary (Unabridged) M4B + EPUB")));
        }

        [Fact]
        public void BareCbr_MeansBitrateWhenAudioMarkersPresent()
        {
            // "CBR" is constant bitrate in audiobook release names — only a comic when nothing says audio.
            var filter = new AudiobookOnlyFilter();
            Assert.False(filter.ShouldFilter(Result("The Number of the Beast (Unabridged) MP3 CBR 64kbps")));
            Assert.False(filter.ShouldFilter(Result("Iron Maiden - Piece of Mind Audiobook 128 kbps CBR")));
            Assert.True(filter.ShouldFilter(Result("Iron Maiden 001 (2019) (Webrip) (The Last Kryptonian-DCP) cbr")));
        }

        [Fact]
        public void LooksLikeUndottedComicOrEbookRelease_PlainTitles_AreNotFlagged()
        {
            Assert.False(AudiobookOnlyFilter.LooksLikeUndottedComicOrEbookRelease("The Iron Maiden - Robert Anthony"));
            Assert.False(AudiobookOnlyFilter.LooksLikeUndottedComicOrEbookRelease("Digital Fortress (Unabridged)"));
            Assert.False(AudiobookOnlyFilter.LooksLikeUndottedComicOrEbookRelease("Two of a Kind (2 of 2 parts)"));
        }

        [Fact]
        public void MusicSceneSingle_NoAudioSignal_IsFiltered()
        {
            // Live case: a techno single grabbed for the generic audiobook "Titans".
            var filter = new AudiobookOnlyFilter();
            Assert.True(filter.ShouldFilter(Result("Artist-Clash of the Titans-[IVT075]-SINGLE-WEB-2026-PTC")));
        }

        [Fact]
        public void MusicSceneAlbumFlac_NoAudioSignal_IsFiltered()
        {
            var filter = new AudiobookOnlyFilter();
            Assert.True(filter.ShouldFilter(Result("Some Band - Greatest Hits-ALBUM-FLAC-2025-GRP")));
        }

        [Fact]
        public void OrdinaryTitleContainingSingleWord_IsNotFiltered()
        {
            // "Single Malt" must not trip the scene-grammar guard (needs the delimited pair).
            var filter = new AudiobookOnlyFilter();
            Assert.False(filter.ShouldFilter(Result("The Single Malt Mystery (Unabridged)")));
        }

        [Fact]
        public void AudiobookWithRuntime_EvenWithSceneTokens_IsNotFiltered()
        {
            var filter = new AudiobookOnlyFilter();
            var r = Result("Weird-SINGLE-WEB-Title");
            r.Runtime = 720; // positive audio evidence wins
            Assert.False(filter.ShouldFilter(r));
        }

        [Fact]
        public void ComicExtension_NoAudioSignal_IsFiltered()
        {
            var filter = new AudiobookOnlyFilter();
            Assert.True(filter.ShouldFilter(Result("Tom Corbett Space Cadet V2 001(2013)(Digital)(TLK EMPIRE HD).cbr ( Nem )")));
        }

        [Fact]
        public void EbookExtension_NoAudioSignal_IsFiltered()
        {
            var filter = new AudiobookOnlyFilter();
            Assert.True(filter.ShouldFilter(Result("Author - Great Book.epub")));
        }
    }
}
