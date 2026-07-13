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
using System.Globalization;
using System.Text.RegularExpressions;
using HtmlAgilityPack;
using Listenarr.Application.Metadata.Audible;

namespace Listenarr.Infrastructure.Metadata.Providers.Amazon
{
    /// <summary>
    /// Pure HTML→metadata parse of an Amazon audiobook product page (dp page),
    /// selector-anchored to the stable widget ids/attributes Amazon has used
    /// for years: <c>#productTitle</c>, the <c>#bylineInfo</c> contributor
    /// spans with "(Author)/(Narrator)/(Publisher)" roles, and the
    /// <c>data-rpi-attribute-name</c> detail blocks (narrator, listening
    /// length, publication date, language, publisher). Returns null unless a
    /// title parses — a page without one is a robot wall, a redirect, or
    /// markup drift, and a half-parsed answer is worse than none.
    /// </summary>
    public static class AmazonProductPageParser
    {
        // "17 hours and 38 minutes" / "38 minutes" / "17 hours"
        private static readonly Regex ListeningLengthRegex = new(
            @"(?:(?<h>\d+)\s*hours?)?\s*(?:and\s*)?(?:(?<m>\d+)\s*minutes?)?",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static AudibleBookResponse? Parse(string? html, string asin)
        {
            if (string.IsNullOrWhiteSpace(html)) return null;

            var doc = new HtmlDocument();
            doc.LoadHtml(html);
            var root = doc.DocumentNode;

            var title = InnerTextOf(root.SelectSingleNode("//span[@id='productTitle']"));
            if (string.IsNullOrWhiteSpace(title)) return null;

            var (authors, narrators, bylinePublisher) = ParseByline(root);

            // The rpi detail blocks repeat/augment the byline and carry the
            // audiobook specifics the byline lacks.
            string? Rpi(string name) => InnerTextOf(root.SelectSingleNode(
                $"//div[@data-rpi-attribute-name='{name}']//div[contains(@class,'rpi-attribute-value')]"));

            var rpiAuthor = Rpi("audiobook_details-author");
            if (authors.Count == 0 && !string.IsNullOrWhiteSpace(rpiAuthor)) authors.Add(rpiAuthor!);
            var rpiNarrator = Rpi("audiobook_details-narrator");
            if (narrators.Count == 0 && !string.IsNullOrWhiteSpace(rpiNarrator)) narrators.Add(rpiNarrator!);

            var publisher = Rpi("book_details-publisher") ?? bylinePublisher;
            var language = Rpi("language");
            var publicationDate = ParseDate(Rpi("book_details-publication_date"));
            var lengthMinutes = ParseListeningLength(Rpi("audiobook_details-listening_length"));
            var version = Rpi("audiobook_details-version"); // "Unabridged"/"Abridged"

            var imageUrl = ParseCover(root);
            var description = ParseDescription(root);

            return new AudibleBookResponse
            {
                Asin = asin,
                Title = title,
                Authors = authors.Select(a => new AudibleAuthor { Name = a }).ToList(),
                Narrators = narrators.Select(n => new AudibleNarrator { Name = n }).ToList(),
                Publisher = publisher,
                PublishDate = publicationDate,
                ReleaseDate = publicationDate,
                Description = description,
                ImageUrl = imageUrl,
                LengthMinutes = lengthMinutes,
                Language = language?.ToLowerInvariant(),
                BookFormat = version,
                ContentType = "Product",
                Region = "us",
            };
        }

        /// <summary>
        /// Byline contributors: spans of class "author" holding a name link
        /// plus a "(Role)" contribution marker. Roles seen in the wild:
        /// Author, Narrator, Publisher, Translator, Editor — unknown roles
        /// are ignored rather than guessed.
        /// </summary>
        private static (List<string> Authors, List<string> Narrators, string? Publisher) ParseByline(HtmlNode root)
        {
            var authors = new List<string>();
            var narrators = new List<string>();
            string? publisher = null;

            var spans = root.SelectNodes("//div[@id='bylineInfo']//span[contains(@class,'author')]");
            foreach (var span in spans ?? Enumerable.Empty<HtmlNode>())
            {
                var name = InnerTextOf(span.SelectSingleNode(".//a"));
                var role = InnerTextOf(span.SelectSingleNode(".//span[contains(@class,'contribution')]"));
                if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(role)) continue;

                if (role.Contains("Author", StringComparison.OrdinalIgnoreCase)) authors.Add(name!);
                else if (role.Contains("Narrator", StringComparison.OrdinalIgnoreCase)) narrators.Add(name!);
                else if (role.Contains("Publisher", StringComparison.OrdinalIgnoreCase)) publisher ??= name;
            }

            return (authors, narrators, publisher);
        }

        private static string? ParseCover(HtmlNode root)
        {
            var img = root.SelectSingleNode("//img[@id='landingImage']")
                ?? root.SelectSingleNode("//img[@data-a-image-name='landingImage']");
            if (img == null) return null;

            // data-old-hires is the full-resolution asset; the dynamic-image
            // map's first URL is the fallback.
            var hires = img.GetAttributeValue("data-old-hires", null);
            if (!string.IsNullOrWhiteSpace(hires)) return hires;

            var dynamicMap = img.GetAttributeValue("data-a-dynamic-image", null);
            if (!string.IsNullOrWhiteSpace(dynamicMap))
            {
                var match = Regex.Match(HtmlEntity.DeEntitize(dynamicMap), @"https://[^""\s]+");
                if (match.Success) return match.Value;
            }

            var src = img.GetAttributeValue("src", null);
            return string.IsNullOrWhiteSpace(src) ? null : src;
        }

        /// <summary>
        /// Book description: the expander content inside
        /// bookDescription_feature_div, kept as inner HTML — Audible-sourced
        /// descriptions are HTML too, and the UI renders both the same way.
        /// </summary>
        private static string? ParseDescription(HtmlNode root)
        {
            var node = root.SelectSingleNode(
                "//div[@id='bookDescription_feature_div']//div[contains(@class,'a-expander-content')]");
            var inner = node?.InnerHtml?.Trim();
            return string.IsNullOrWhiteSpace(inner) ? null : inner;
        }

        internal static int? ParseListeningLength(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var match = ListeningLengthRegex.Match(text);
            if (!match.Success) return null;
            var hours = match.Groups["h"].Success ? int.Parse(match.Groups["h"].Value) : 0;
            var minutes = match.Groups["m"].Success ? int.Parse(match.Groups["m"].Value) : 0;
            var total = hours * 60 + minutes;
            return total > 0 ? total : null;
        }

        /// <summary>"December 7, 2023" → "2023-12-07"; unparseable input passes through raw.</summary>
        internal static string? ParseDate(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            return DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed.ToString("yyyy-MM-dd")
                : text;
        }

        private static string? InnerTextOf(HtmlNode? node)
        {
            if (node == null) return null;
            var text = HtmlEntity.DeEntitize(node.InnerText ?? string.Empty);
            text = Regex.Replace(text, @"\s+", " ").Trim();
            return text.Length == 0 ? null : text;
        }
    }
}
