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

namespace Listenarr.Tests.Features.Api.Features.Library
{
    [Trait("Area", "LibraryApi")]
    [Trait("Name", "LibraryController_ConcurrentAddTests")]
    [Trait("Category", "LibraryController")]
    public class LibraryController_ConcurrentAddTests : BaseTests
    {
        [Fact]
        [Trait("Scenario", "ConcurrentAddsSameAsin_CreateOneRecord")]
        public async Task ConcurrentAdds_SameAsin_CreateExactlyOneRecord()
        {
            AudiobookAddLockManager.ResetForTesting();
            var controller = _provider.GetRequiredService<LibraryController>();

            const string asin = "B00CONCURRENT1";
            LibraryController.AddToLibraryRequest MakeRequest() => new()
            {
                Metadata = new AudibleBookMetadata
                {
                    Title = "Race Condition: A Love Story",
                    Asin = asin,
                    Authors = new List<string> { "Test Author" }
                },
                Monitored = true,
                AutoSearch = false
            };

            // Fire two adds for the same ASIN concurrently. Without the per-ASIN
            // lock, both pass the dedup check before either inserts → two rows.
            var results = await Task.WhenAll(
                Task.Run(() => controller.AddToLibrary(MakeRequest())),
                Task.Run(() => controller.AddToLibrary(MakeRequest())));

            Assert.NotNull(results[0]);
            Assert.NotNull(results[1]);

            var matches = (await _audiobookRepository.GetAllAsync())
                .Where(a => string.Equals(a.Asin, asin, StringComparison.OrdinalIgnoreCase))
                .ToList();
            Assert.Single(matches);
        }
    }
}
