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
using Listenarr.Application.Common;
using Listenarr.Application.Interfaces;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Listenarr.Tests.Features.Api.Services
{
    /// <summary>
    /// Pins the inputs the migration dry-run (tools/migration-dryrun.py) relies on for
    /// re-deriving per-book BasePaths. If this test fails, the Python port of
    /// ApplyNamingPattern + SanitizePathComponent in migration-dryrun.py is out of sync
    /// with the live service, and any migration UPDATE driven by it will write paths the
    /// running app would not have produced — re-introducing drift.
    ///
    /// Update the expected strings on this side AND the Python port together.
    /// </summary>
    public class FileNamingService_MigrationParityTests
    {
        private static FileNamingService NewService() =>
            new FileNamingService(
                Mock.Of<IConfigurationService>(),
                NullLogger<FileNamingService>.Instance);

        private const string FolderPattern = "{Author}/{Series}/{Title}/{Narrator}";

        public static TheoryData<string, string, string, string, string> Cases() =>
            new()
            {
                // (Author, Series, Title, Narrator, expectedFolder)
                { "Stephen King", "The Dark Tower", "The Gunslinger", "George Guidall",
                  "Stephen King/The Dark Tower/The Gunslinger/George Guidall" },

                // colon in title -> ' - '
                { "Stephen King", "The Dark Tower", "The Dark Tower I: The Gunslinger", "George Guidall",
                  "Stephen King/The Dark Tower/The Dark Tower I - The Gunslinger/George Guidall" },

                // adjacent-duplicate component collapse (Series == Title)
                { "Stephen King", "The Dark Tower", "The Dark Tower", "George Guidall",
                  "Stephen King/The Dark Tower/George Guidall" },

                { "Stephen King", "The Shining", "Doctor Sleep", "Will Patton",
                  "Stephen King/The Shining/Doctor Sleep/Will Patton" },

                // adjacent-duplicate collapse (Series == Title, again)
                { "Stephen King", "The Shining", "The Shining", "Campbell Scott",
                  "Stephen King/The Shining/Campbell Scott" },

                // empty {Series} sentinel + slash cleanup; parens preserved
                { "Charles Dickens", "", "A Tale of Two Cities (Annotated)", "Virtual Voice",
                  "Charles Dickens/A Tale of Two Cities (Annotated)/Virtual Voice" },

                { "Charles Dickens", "", "A Tale of Two Cities", "Jim Weiss",
                  "Charles Dickens/A Tale of Two Cities/Jim Weiss" },

                { "Stephen King", "Holly Gibney", "The Outsider", "Will Patton",
                  "Stephen King/Holly Gibney/The Outsider/Will Patton" },

                // already-sanitized author "Stephen King - introduction" (mirrors live data shape)
                { "Stephen King - introduction", "", "Nightmare at 20,000 Feet", "Julia Campbell",
                  "Stephen King - introduction/Nightmare at 20,000 Feet/Julia Campbell" },

                // parens in narrator preserved
                { "Neal Stephenson", "The Baroque Cycle", "Quicksilver", "Neal Stephenson (introduction)",
                  "Neal Stephenson/The Baroque Cycle/Quicksilver/Neal Stephenson (introduction)" },
            };

        [Theory]
        [MemberData(nameof(Cases))]
        public void ApplyNamingPattern_FolderShape_MatchesMigrationDryRun(
            string author, string series, string title, string narrator, string expected)
        {
            var svc = NewService();
            var variables = new Dictionary<string, object>
            {
                { "Author", author },
                { "Series", series },
                { "Title", title },
                { "Narrator", narrator },
            };

            var actual = svc.ApplyNamingPattern(FolderPattern, variables, treatAsFilename: false);

            Assert.Equal(expected, actual);
        }
    }
}
