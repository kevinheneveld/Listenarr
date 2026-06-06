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
using Xunit;

namespace Listenarr.Tests.Features.Api.Services
{
    public class ScanBackgroundService_BasePathGuardTests
    {
        private static readonly string[] Roots = new[] { "/audiobooks" };

        [Fact]
        public void Rejects_Candidate_That_Equals_A_Configured_Root()
        {
            Assert.False(
                ScanBackgroundService.IsAcceptableScannedBasePath(
                    candidate: "/audiobooks",
                    currentBasePath: null,
                    configuredRootPaths: Roots));
        }

        [Fact]
        public void Rejects_Candidate_That_Equals_A_Configured_Root_With_Trailing_Slash()
        {
            Assert.False(
                ScanBackgroundService.IsAcceptableScannedBasePath(
                    candidate: "/audiobooks/",
                    currentBasePath: null,
                    configuredRootPaths: Roots));
        }

        [Fact]
        public void Rejects_Author_Level_Candidate_That_Would_Broaden_Existing_Per_Book_BasePath()
        {
            // Migration target shape: an audiobook whose BasePath was repaired to per-book
            // ('/audiobooks/Stephen King/The Outsider') must not be re-broadened back to author level
            // ('/audiobooks/Stephen King') when a later scan's LCA across mixed-book matches lands there.
            Assert.False(
                ScanBackgroundService.IsAcceptableScannedBasePath(
                    candidate: "/audiobooks/Stephen King",
                    currentBasePath: "/audiobooks/Stephen King/The Outsider",
                    configuredRootPaths: Roots));
        }

        [Fact]
        public void Rejects_Root_Level_Candidate_Even_When_Current_BasePath_Is_Per_Book()
        {
            Assert.False(
                ScanBackgroundService.IsAcceptableScannedBasePath(
                    candidate: "/audiobooks",
                    currentBasePath: "/audiobooks/Stephen King/The Outsider",
                    configuredRootPaths: Roots));
        }

        [Fact]
        public void Accepts_Per_Book_Candidate_When_Current_BasePath_Is_Null()
        {
            Assert.True(
                ScanBackgroundService.IsAcceptableScannedBasePath(
                    candidate: "/audiobooks/Stephen King/The Outsider",
                    currentBasePath: null,
                    configuredRootPaths: Roots));
        }

        [Fact]
        public void Accepts_Per_Book_Candidate_When_Current_BasePath_Is_Itself_A_Root()
        {
            // A legacy row whose BasePath is the bare root benefits from the first scan-derived
            // per-book LCA; we only protect *already-per-book* BasePaths from being broadened.
            Assert.True(
                ScanBackgroundService.IsAcceptableScannedBasePath(
                    candidate: "/audiobooks/Stephen King/The Outsider",
                    currentBasePath: "/audiobooks",
                    configuredRootPaths: Roots));
        }

        [Fact]
        public void Accepts_Candidate_That_Is_Deeper_Than_Current_BasePath()
        {
            // Refinement (CD-level path under an existing per-book BasePath) is not a broadening.
            Assert.True(
                ScanBackgroundService.IsAcceptableScannedBasePath(
                    candidate: "/audiobooks/Stephen King/The Outsider/CD1",
                    currentBasePath: "/audiobooks/Stephen King/The Outsider",
                    configuredRootPaths: Roots));
        }

        [Fact]
        public void Accepts_Candidate_Equal_To_Current_BasePath()
        {
            // Equal-to-current is a no-op acceptance: not a broadening.
            Assert.True(
                ScanBackgroundService.IsAcceptableScannedBasePath(
                    candidate: "/audiobooks/Stephen King/The Outsider",
                    currentBasePath: "/audiobooks/Stephen King/The Outsider",
                    configuredRootPaths: Roots));
        }

        [Fact]
        public void Rejects_Null_Or_Empty_Candidate()
        {
            Assert.False(ScanBackgroundService.IsAcceptableScannedBasePath(null, null, Roots));
            Assert.False(ScanBackgroundService.IsAcceptableScannedBasePath(string.Empty, null, Roots));
            Assert.False(ScanBackgroundService.IsAcceptableScannedBasePath("   ", null, Roots));
        }

        [Fact]
        public void Tolerates_Null_Or_Empty_RootPaths_Collection()
        {
            // When no roots are configured (or enumeration failed in the caller), only the
            // broaden-existing rule applies. A reasonable per-book candidate still accepts.
            Assert.True(
                ScanBackgroundService.IsAcceptableScannedBasePath(
                    candidate: "/audiobooks/Stephen King/The Outsider",
                    currentBasePath: null,
                    configuredRootPaths: null));

            Assert.True(
                ScanBackgroundService.IsAcceptableScannedBasePath(
                    candidate: "/audiobooks/Stephen King/The Outsider",
                    currentBasePath: null,
                    configuredRootPaths: System.Array.Empty<string>()));
        }

        [Fact]
        public void Honors_Multiple_Roots_When_Configured()
        {
            var roots = new[] { "/audiobooks", "/audiobooks-archive" };

            Assert.False(
                ScanBackgroundService.IsAcceptableScannedBasePath(
                    candidate: "/audiobooks-archive",
                    currentBasePath: null,
                    configuredRootPaths: roots));

            Assert.True(
                ScanBackgroundService.IsAcceptableScannedBasePath(
                    candidate: "/audiobooks-archive/Asimov/Foundation",
                    currentBasePath: null,
                    configuredRootPaths: roots));
        }

        [Fact]
        public void Trailing_Separators_Do_Not_Defeat_Broaden_Check()
        {
            Assert.False(
                ScanBackgroundService.IsAcceptableScannedBasePath(
                    candidate: "/audiobooks/Stephen King/",
                    currentBasePath: "/audiobooks/Stephen King/The Outsider/",
                    configuredRootPaths: Roots));
        }
    }
}
