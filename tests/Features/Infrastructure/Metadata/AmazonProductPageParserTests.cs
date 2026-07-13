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
using Listenarr.Infrastructure.Metadata.Providers.Amazon;

namespace Listenarr.Tests.Features.Infrastructure.Metadata
{
    /// <summary>
    /// Fixture HTML mirrors the live Amazon dp page that motivated the
    /// feature (an Amazon-exclusive audiobook edition absent from all ten
    /// regional Audible catalogs): the byline contributor spans, the
    /// data-rpi-attribute detail blocks, landingImage, and the book
    /// description expander — verbatim structure, trimmed content.
    /// </summary>
    [Trait("Area", "Metadata")]
    [Trait("Name", "AmazonProductPageParserTests")]
    public class AmazonProductPageParserTests
    {
        private const string ProductPage = """
            <html><body>
            <span id="productTitle" class="a-size-large">  Foundation and Earth: Foundation, Book 7  </span>
            <div id="bylineInfo" class="a-section">
              <span class="author notFaded">
                <a class="a-link-normal" href="/s/x">Isaac Asimov</a>
                <span class="contribution"><span class="a-color-secondary">(Author), </span></span>
              </span>
              <span class="author notFaded">
                <a class="a-link-normal" href="/s/x">William Hope</a>
                <span class="contribution"><span class="a-color-secondary">(Narrator), </span></span>
              </span>
              <span class="author notFaded">
                <a class="a-link-normal" href="/s/x">HarperVoyager</a>
                <span class="contribution"><span class="a-color-secondary">(Publisher)</span></span>
              </span>
              <span class="a-color-secondary">Format: </span><span>Audible Audiobook</span>
            </div>
            <img id="landingImage" data-old-hires="https://m.media-amazon.com/images/I/91gx320WyvL._SL1500_.jpg"
                 data-a-dynamic-image="{&quot;https://m.media-amazon.com/images/I/91gx320WyvL._SX342_.jpg&quot;:[342,342]}" src="x.jpg"/>
            <div id="rpi-attribute-audiobook_details-listening_length" data-rpi-attribute-name="audiobook_details-listening_length" class="a-section rpi-attribute-content">
              <div class="rpi-attribute-label"><span>Listening Length</span></div>
              <div class="a-section a-spacing-none a-text-center rpi-attribute-value"><span>17 hours and 38 minutes</span></div>
            </div>
            <div id="rpi-attribute-book_details-publication_date" data-rpi-attribute-name="book_details-publication_date" class="a-section rpi-attribute-content">
              <div class="a-section rpi-attribute-value"><span>December 7, 2023</span></div>
            </div>
            <div id="rpi-attribute-language" data-rpi-attribute-name="language" class="a-section rpi-attribute-content">
              <div class="a-section rpi-attribute-value"><span>English</span></div>
            </div>
            <div id="rpi-attribute-book_details-publisher" data-rpi-attribute-name="book_details-publisher" class="a-section rpi-attribute-content">
              <div class="a-section rpi-attribute-value"><span>HarperVoyager</span></div>
            </div>
            <div id="rpi-attribute-audiobook_details-version" data-rpi-attribute-name="audiobook_details-version" class="a-section rpi-attribute-content">
              <div class="a-section rpi-attribute-value"><span>Unabridged</span></div>
            </div>
            <div id="bookDescription_feature_div">
              <div class="a-expander-collapsed-height a-row a-expander-container">
                <div data-expanded="false" class="a-expander-content a-expander-partial-collapse-content">
                  <p><span>Faced with determining the fate of the galaxy...</span></p>
                </div>
              </div>
            </div>
            </body></html>
            """;

        [Fact]
        public void Parse_FullProductPage_MapsAllFields()
        {
            var result = AmazonProductPageParser.Parse(ProductPage, "B0CHK9ZJPB");

            Assert.NotNull(result);
            Assert.Equal("B0CHK9ZJPB", result!.Asin);
            Assert.Equal("Foundation and Earth: Foundation, Book 7", result.Title);
            Assert.Equal(new[] { "Isaac Asimov" }, result.Authors!.Select(a => a.Name));
            Assert.Equal(new[] { "William Hope" }, result.Narrators!.Select(n => n.Name));
            Assert.Equal("HarperVoyager", result.Publisher);
            Assert.Equal("2023-12-07", result.ReleaseDate);
            Assert.Equal(17 * 60 + 38, result.LengthMinutes);
            Assert.Equal("english", result.Language);
            Assert.Equal("Unabridged", result.BookFormat);
            Assert.Equal("https://m.media-amazon.com/images/I/91gx320WyvL._SL1500_.jpg", result.ImageUrl);
            Assert.Contains("fate of the galaxy", result.Description);
        }

        [Fact]
        public void Parse_NoTitle_ReturnsNull()
        {
            // A robot wall / redirect / drifted page has no #productTitle —
            // a half-parsed answer is worse than none.
            Assert.Null(AmazonProductPageParser.Parse("<html><body><div id='bylineInfo'>x</div></body></html>", "B000X"));
            Assert.Null(AmazonProductPageParser.Parse("", "B000X"));
            Assert.Null(AmazonProductPageParser.Parse(null, "B000X"));
        }

        [Fact]
        public void Parse_RpiBlocksFillMissingByline()
        {
            // Some layouts carry contributors only in the rpi carousel.
            const string html = """
                <html><body>
                <span id="productTitle">Some Book</span>
                <div data-rpi-attribute-name="audiobook_details-author" class="rpi-attribute-content">
                  <div class="rpi-attribute-value"><a href="/s"><span>Jane Writer</span></a></div>
                </div>
                <div data-rpi-attribute-name="audiobook_details-narrator" class="rpi-attribute-content">
                  <div class="rpi-attribute-value"><a href="/s"><span>John Reader</span></a></div>
                </div>
                </body></html>
                """;

            var result = AmazonProductPageParser.Parse(html, "B000Y");

            Assert.NotNull(result);
            Assert.Equal(new[] { "Jane Writer" }, result!.Authors!.Select(a => a.Name));
            Assert.Equal(new[] { "John Reader" }, result.Narrators!.Select(n => n.Name));
        }

        [Fact]
        public void Parse_CoverFallsBackToDynamicImageMap()
        {
            const string html = """
                <html><body>
                <span id="productTitle">Some Book</span>
                <img id="landingImage" data-a-dynamic-image="{&quot;https://m.media-amazon.com/images/I/abc._SX342_.jpg&quot;:[342,342]}"/>
                </body></html>
                """;

            var result = AmazonProductPageParser.Parse(html, "B000Z");

            Assert.Equal("https://m.media-amazon.com/images/I/abc._SX342_.jpg", result!.ImageUrl);
        }

        [Theory]
        [InlineData("17 hours and 38 minutes", 1058)]
        [InlineData("17 hours", 1020)]
        [InlineData("38 minutes", 38)]
        [InlineData("1 hour and 1 minute", 61)]
        [InlineData("garbage", null)]
        [InlineData(null, null)]
        public void ParseListeningLength_HandlesAllShapes(string? text, int? expected)
        {
            Assert.Equal(expected, AmazonProductPageParser.ParseListeningLength(text));
        }

        [Theory]
        [InlineData("December 7, 2023", "2023-12-07")]
        [InlineData("not a date", "not a date")]
        [InlineData(null, null)]
        public void ParseDate_NormalizesToIso(string? text, string? expected)
        {
            Assert.Equal(expected, AmazonProductPageParser.ParseDate(text));
        }
    }
}
