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

using Listenarr.Application.Audiobooks;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Application.Audiobooks
{
    [Trait("Name", "AuthorNameMatcherTests")]
    [Trait("Category", "Library")]
    public class AuthorNameMatcherTests : BaseTests
    {
        [Theory]
        [InlineData("Robert A. Heinlein", "Robert Heinlein")]
        [InlineData("Jim Butcher - editor", "Jim Butcher")]
        [InlineData("Stephen King - introduction", "Stephen King")]
        [InlineData("W.E.B. Griffin", "W. E. B. Griffin")]
        [InlineData("C.S. Lewis", "C. S. Lewis")]
        public void SharesName_Variants_MatchTheSamePerson(string a, string b)
        {
            Assert.True(AuthorNameMatcher.SharesName(a, b));
            Assert.True(AuthorNameMatcher.SharesName(b, a));
        }

        [Theory]
        [InlineData("Fonda Lee", "James Patterson")]
        [InlineData("Lauren Rothery", "Dean Koontz")]
        [InlineData("", "Dean Koontz")]
        [InlineData("Dean Koontz", null)]
        public void SharesName_DifferentPeopleOrBlank_DoNotMatch(string? a, string? b)
        {
            Assert.False(AuthorNameMatcher.SharesName(a, b));
        }

        [Fact]
        public void SharesAnyName_ChecksEveryCandidate()
        {
            Assert.True(AuthorNameMatcher.SharesAnyName("James Patterson", new[] { "Fonda Lee", "James Patterson" }));
            Assert.False(AuthorNameMatcher.SharesAnyName("James Patterson", new[] { "Fonda Lee" }));
            Assert.False(AuthorNameMatcher.SharesAnyName("James Patterson", null));
        }

        [Fact]
        public void Normalize_StripsPunctuationAndCase()
        {
            Assert.Equal("robert a heinlein", AuthorNameMatcher.Normalize("  Robert A. Heinlein "));
            Assert.Equal(string.Empty, AuthorNameMatcher.Normalize(null));
        }
    }
}
