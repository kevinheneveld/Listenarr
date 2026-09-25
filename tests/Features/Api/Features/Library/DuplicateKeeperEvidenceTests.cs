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
using Listenarr.Api.Features.Library;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Api.Features.Library
{
    /// <summary>
    /// Narrator-credit agreement under speech-to-text mangling: the names
    /// whisper produced on the live library must agree with the catalog
    /// spelling, while genuinely different narrators must not.
    /// </summary>
    [Trait("Area", "LibraryApi")]
    [Trait("Name", "DuplicateKeeperEvidenceTests")]
    [Trait("Category", "LibraryController")]
    public class DuplicateKeeperEvidenceTests : BaseTests
    {
        [Theory]
        [InlineData("Edoardo Ballerini", "Eduardo Valarini")]
        [InlineData("Anne Flosnik", "Aunt Blasnick")]
        [InlineData("Jennifer Ikeda", "Jennifer Eketa")]
        [InlineData("Dick Hill", "dick hill")]
        [InlineData("James Marsters", "James Masters")]
        [InlineData("Simon Vance", "Simon Vance.")]
        [InlineData("Kate Reading", "Kate Reading and Michael Kramer")]
        [InlineData("Joshua Kane", "Joshua Caine")]
        [InlineData("Danny Mastrogiorgio", "Danny Master of Giorgio")]
        public void NamesAgree_AcceptsSttMangledSpellingsOfTheSameNarrator(string record, string heard)
        {
            Assert.True(DuplicateKeeperEvidence.NamesAgree(record, heard));
        }

        [Theory]
        [InlineData("Jeff Harding", "Dick Hill")]
        [InlineData("Dick Hill", "Jeff Hall")]
        [InlineData("Michael Ward", "Tim Curry")]
        [InlineData("Bill Homewood", "Tim Curry")]
        [InlineData("Michael Kramer", "Terrence Asilford")]
        [InlineData("Avita Jay", "Aunt Blasnick")]
        public void NamesAgree_RejectsDifferentNarrators(string record, string heard)
        {
            Assert.False(DuplicateKeeperEvidence.NamesAgree(record, heard));
        }

        [Fact]
        public void NamesAgree_ManyTokenCredit_TerminatesAndRejects()
        {
            // The split-surname re-join once appended to the list it iterated
            // and never terminated on any multi-word credit (live OOM crash
            // loop, exit 137). Bounded now: ten tokens, instant answer.
            var task = System.Threading.Tasks.Task.Run(() => DuplicateKeeperEvidence.NamesAgree(
                "Dick Hill",
                "Fool Moon by Jim Butcher print publication by Roc a division of Penguin Putnam"));
            Assert.True(task.Wait(TimeSpan.FromSeconds(5)), "NamesAgree did not terminate");
            Assert.False(task.Result);
        }

        [Theory]
        [InlineData("ballerini", "valarini", true)]
        [InlineData("flosnik", "blasnick", true)]
        [InlineData("hill", "hall", false)]
        [InlineData("kane", "caine", true)]
        [InlineData("lee", "leo", false)]
        [InlineData("dick", "dirk", false)]
        [InlineData("harding", "hill", false)]
        public void TokensAgree_FoldsConfusableConsonantsOnLongTokensOnly(string a, string b, bool expected)
        {
            Assert.Equal(expected, DuplicateKeeperEvidence.TokensAgree(a, b));
        }
    }
}
