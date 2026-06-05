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
using Listenarr.Infrastructure.Adapters;
using Xunit;

namespace Listenarr.Tests.Features.Infrastructure.Adapters
{
    /// <summary>
    /// Tests for qBittorrent category filtering functionality
    /// Verifies that the adapter correctly applies category filters to API calls
    /// </summary>
    public class QbittorrentCategoryFilteringTests
    {
        [Fact]
        public void QBittorrentHelpers_BuildCategoryParameter_With_Category_Returns_Formatted_Parameter()
        {
            // Arrange
            var settings = new Dictionary<string, object> { { "category", "audiobooks" } };

            // Act
            var result = QBittorrentHelpers.BuildCategoryParameter(settings, "&");

            // Assert
            Assert.Equal("&category=audiobooks", result);
        }

        [Fact]
        public void QBittorrentHelpers_BuildCategoryParameter_Without_Category_Returns_Empty_String()
        {
            // Arrange
            var settings = new Dictionary<string, object>();

            // Act
            var result = QBittorrentHelpers.BuildCategoryParameter(settings, "&");

            // Assert
            Assert.Empty(result);
        }

        [Fact]
        public void QBittorrentHelpers_BuildCategoryParameter_With_Null_Settings_Returns_Empty_String()
        {
            // Act
            var result = QBittorrentHelpers.BuildCategoryParameter(null, "&");

            // Assert
            Assert.Empty(result);
        }

        [Fact]
        public void QBittorrentHelpers_BuildCategoryParameter_Uses_Correct_Prefix_Question()
        {
            // Arrange
            var settings = new Dictionary<string, object> { { "category", "test" } };

            // Act
            var result = QBittorrentHelpers.BuildCategoryParameter(settings, "?");

            // Assert
            Assert.Equal("?category=test", result);
        }

        [Fact]
        public void QBittorrentHelpers_BuildCategoryParameter_Uses_Correct_Prefix_Ampersand()
        {
            // Arrange
            var settings = new Dictionary<string, object> { { "category", "test" } };

            // Act
            var result = QBittorrentHelpers.BuildCategoryParameter(settings, "&");

            // Assert
            Assert.Equal("&category=test", result);
        }

        [Fact]
        public void QBittorrentHelpers_BuildCategoryParameter_With_Special_Characters_AreURLEncoded()
        {
            // Arrange
            var settings = new Dictionary<string, object> { { "category", "audio books & stuff" } };

            // Act
            var result = QBittorrentHelpers.BuildCategoryParameter(settings, "&");

            // Assert
            // The spaces and & should be URL encoded
            Assert.Equal("&category=audio%20books%20%26%20stuff", result);
        }

        [Fact]
        public void QBittorrentHelpers_BuildCategoryParameter_Trims_Configured_Category()
        {
            // Arrange
            var settings = new Dictionary<string, object> { { "category", "  audiobooks  " } };

            // Act
            var result = QBittorrentHelpers.BuildCategoryParameter(settings, "&");

            // Assert
            Assert.Equal("&category=audiobooks", result);
        }

        [Fact]
        public void QBittorrentHelpers_BuildCategoryParameter_With_Empty_Category_String_Returns_Empty()
        {
            // Arrange
            var settings = new Dictionary<string, object> { { "category", "" } };

            // Act
            var result = QBittorrentHelpers.BuildCategoryParameter(settings, "&");

            // Assert
            Assert.Empty(result);
        }

        [Fact]
        public void QBittorrentHelpers_BuildCategoryParameter_With_Whitespace_Category_Returns_Empty()
        {
            // Arrange
            var settings = new Dictionary<string, object> { { "category", "   " } };

            // Act
            var result = QBittorrentHelpers.BuildCategoryParameter(settings, "&");

            // Assert
            Assert.Empty(result);
        }

        [Fact]
        public void QBittorrentHelpers_BuildCategoryParameter_With_Null_Category_Value_Returns_Empty()
        {
            // Arrange
            var settings = new Dictionary<string, object> { { "category", null } };

            // Act
            var result = QBittorrentHelpers.BuildCategoryParameter(settings, "&");

            // Assert
            Assert.Empty(result);
        }
    }

    /// <summary>
    /// Tests for qBittorrent completion detection.
    /// Guards against the metaDL false-completion bug where a torrent still fetching its
    /// metadata reports amount_left == 0 (size unknown) and was treated as complete, which
    /// enqueued an import that found 0 files and landed the download in ImportBlocked.
    /// </summary>
    public class QbittorrentCompletionTests
    {
        [Fact]
        public void IsTorrentComplete_When_MetaDL_AmountLeftZero_UnknownSize_Returns_False()
        {
            // metaDL torrent: progress 0, amount_left 0 (size not yet known) — NOT complete.
            Assert.False(QBittorrentHelpers.IsTorrentComplete(progress: 0.0, amountLeft: 0L, size: 0L));
        }

        [Fact]
        public void IsTorrentComplete_When_FullProgress_Returns_True()
        {
            // A genuinely finished torrent: progress at 100%.
            Assert.True(QBittorrentHelpers.IsTorrentComplete(progress: 1.0, amountLeft: 0L, size: 1_048_576L));
        }

        [Fact]
        public void IsTorrentComplete_When_AmountLeftZero_KnownSize_Returns_True()
        {
            // Complete torrent reported via the amount_left heuristic with a known size.
            Assert.True(QBittorrentHelpers.IsTorrentComplete(progress: 0.999, amountLeft: 0L, size: 1_048_576L));
        }

        [Fact]
        public void IsTorrentComplete_When_Downloading_AmountRemaining_Returns_False()
        {
            // In-progress download: bytes still remaining.
            Assert.False(QBittorrentHelpers.IsTorrentComplete(progress: 0.5, amountLeft: 500_000L, size: 1_000_000L));
        }
    }
}

