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
using Listenarr.Application.Search;
using Listenarr.Domain.Models;
using Xunit;

namespace Listenarr.Tests.Features.Api.Services
{
    public class SearchQueryBuilderTests
    {
        private static Audiobook Book(string title, params string[] authors) =>
            new() { Title = title, Authors = authors.ToList() };

        [Fact]
        public void Build_StripsVolumeSuffixAndDropsSeries_FixesHarryPotterRegression()
        {
            // The exact live failure: raw query was
            // "Harry Potter and the Goblet of Fire, Book 4 J.K. Rowling Harry Potter" -> 0 results.
            var book = Book("Harry Potter and the Goblet of Fire, Book 4", "J.K. Rowling");
            book.Series = "Harry Potter";

            var query = SearchQueryBuilder.Build(book);

            Assert.Equal("Harry Potter and the Goblet of Fire J.K. Rowling", query);
            Assert.DoesNotContain("Book 4", query);
            // Series "Harry Potter" must not be appended a second time.
            Assert.Equal(1, CountOccurrences(query, "Harry Potter"));
        }

        [Fact]
        public void Build_StripsParentheticalEditionTags()
        {
            var book = Book("Harry Potter and the Goblet of Fire, Book 4 (Excerpt)", "J.K. Rowling");
            var query = SearchQueryBuilder.Build(book);
            Assert.Equal("Harry Potter and the Goblet of Fire J.K. Rowling", query);
        }

        [Theory]
        [InlineData("Wax and Wayne, Vol. 3", "Wax and Wayne")]
        [InlineData("The Way of Kings Book 1", "The Way of Kings")]
        [InlineData("Some Saga, Part 2", "Some Saga")]
        [InlineData("A Series Episode 5", "A Series")]
        [InlineData("Dungeon Crawler Carl #6", "Dungeon Crawler Carl")]
        public void CleanTitle_StripsVolumeMarkers(string title, string expected)
        {
            Assert.Equal(expected, SearchQueryBuilder.CleanTitle(title));
        }

        [Fact]
        public void Build_OmitsAuthorAlreadyPresentInTitle()
        {
            var book = Book("Becoming by Michelle Obama", "Michelle Obama");
            var query = SearchQueryBuilder.Build(book);
            Assert.Equal("Becoming by Michelle Obama", query);
        }

        [Fact]
        public void Build_HandlesMissingAuthor()
        {
            var query = SearchQueryBuilder.Build(Book("Project Hail Mary, Book 1"));
            Assert.Equal("Project Hail Mary", query);
        }

        [Fact]
        public void BuildTitleOnly_DropsAuthor()
        {
            var book = Book("Project Hail Mary, Book 1", "Andy Weir");
            Assert.Equal("Project Hail Mary", SearchQueryBuilder.BuildTitleOnly(book));
        }

        [Fact]
        public void CleanTitle_EmptyInputIsSafe()
        {
            Assert.Equal(string.Empty, SearchQueryBuilder.CleanTitle(null));
            Assert.Equal(string.Empty, SearchQueryBuilder.CleanTitle("   "));
        }

        private static int CountOccurrences(string haystack, string needle)
        {
            var count = 0;
            var i = 0;
            while ((i = haystack.IndexOf(needle, i, System.StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                count++;
                i += needle.Length;
            }
            return count;
        }
    }
}
