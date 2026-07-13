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
using Listenarr.Application.Metadata.Audible;
using Listenarr.Application.Search.Audible;

namespace Listenarr.Tests.Features.Application.Search
{
    [Trait("Area", "Search")]
    [Trait("Name", "AudibleAuthorSearchWorkflowTests")]
    public class AudibleAuthorSearchWorkflowTests
    {
        private static AudibleSearchResult Result(params string[] authors) => new()
        {
            Asin = "B000TEST000",
            Title = "The Deep Range",
            Authors = authors.Select(a => new AudibleAuthor { Name = a }).ToList(),
        };

        [Fact]
        public void AuthorAgrees_MatchingAuthor_True()
        {
            Assert.True(AudibleAuthorSearchWorkflow.AuthorAgrees(Result("Arthur C. Clarke"), "Arthur C. Clarke"));
        }

        [Fact]
        public void AuthorAgrees_ContainmentEitherWay_True()
        {
            // "Clarke, Arthur C." style and co-author listings must not be
            // rejected on punctuation or extra names.
            Assert.True(AudibleAuthorSearchWorkflow.AuthorAgrees(Result("Arthur C. Clarke", "Gentry Lee"), "Arthur C. Clarke"));
            Assert.True(AudibleAuthorSearchWorkflow.AuthorAgrees(Result("Arthur Clarke"), "Clarke"));
        }

        [Fact]
        public void AuthorAgrees_DifferentAuthor_False()
        {
            // The gate's whole job: fuzzy keyword search must not leak a
            // confidently different author's book into an editions picker.
            Assert.False(AudibleAuthorSearchWorkflow.AuthorAgrees(Result("Jules Verne"), "Arthur C. Clarke"));
        }

        [Fact]
        public void AuthorAgrees_NoAuthorsListed_True()
        {
            // Missing contributors is catalog sloppiness, not disagreement.
            Assert.True(AudibleAuthorSearchWorkflow.AuthorAgrees(Result(), "Arthur C. Clarke"));
        }
    }
}
