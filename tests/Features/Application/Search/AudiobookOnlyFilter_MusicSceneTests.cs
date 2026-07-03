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
