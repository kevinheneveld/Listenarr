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

using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Listenarr.Tests.Features.Api.Features.Library
{
    /// <summary>
    /// The audit finds books whose stored author ASIN belongs to a different author
    /// than their Authors (relabel leftovers) and the repair drops those ASINs and
    /// re-resolves the book's authors by name.
    /// </summary>
    [Trait("Name", "LibraryController_AuthorAsinAuditTests")]
    [Trait("Category", "Library")]
    public class LibraryController_AuthorAsinAuditTests : BaseTests
    {
        private const string PattersonAsin = "B000APZGGS";
        private const string FondaLeeAsin = "B0FONDA001";

        private async Task<(Audiobook Stale, Audiobook Fine)> ArrangeAsync()
        {
            using var httpClient = new HttpClient();
            var audible = new Mock<AudibleService>(httpClient, NullLogger<AudibleService>.Instance);
            audible
                .Setup(service => service.LookupAuthorAsync("Fonda Lee", It.IsAny<string>()))
                .ReturnsAsync(new AuthorLookupItem { Asin = FondaLeeAsin, Name = "Fonda Lee" });
            Init(builder => builder.WithSingleton<AudibleService>(audible.Object));
            await InitializeAsync();
            await CreateApplicationSettings();

            await _audiobookRepository.UpsertCachedAuthorAsync(new AuthorCacheEntry
            {
                AuthorName = "James Patterson",
                AuthorAsin = PattersonAsin,
                Region = "us"
            });

            var stale = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Cross Fire")
                .WithAuthor("Fonda Lee")
                .WithAuthorAsin(PattersonAsin)
                .Build());
            var fine = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Along Came a Spider")
                .WithAuthor("James Patterson")
                .WithAuthorAsin(PattersonAsin)
                .Build());
            return (stale, fine);
        }

        [Fact]
        public async Task AuditAuthorAsins_ListsOnlyTheBookWhoseAsinBelongsToAnotherAuthor()
        {
            var (stale, _) = await ArrangeAsync();
            var controller = _provider.GetRequiredService<LibraryController>();

            var result = await controller.AuditAuthorAsins(150, CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = Assert.IsType<LibraryMaintenanceWorkflow.AuthorAsinAuditResponse>(ok.Value);
            Assert.Equal(2, payload.BooksChecked);
            Assert.Equal(1, payload.DistinctAsins);
            Assert.Equal(1, payload.ResolvedAsins);
            Assert.Equal(0, payload.UnresolvedAsins);
            Assert.Equal(0, payload.Repaired);
            var flagged = Assert.Single(payload.StaleBooks);
            Assert.Equal(stale.Id, flagged.Id);
            var staleAsin = Assert.Single(flagged.StaleAsins);
            Assert.Equal(PattersonAsin, staleAsin.Asin);
            Assert.Equal("James Patterson", staleAsin.ResolvedName);
        }

        [Fact]
        public async Task RepairAuthorAsins_DropsTheStaleAsinAndReResolvesByName()
        {
            var (stale, fine) = await ArrangeAsync();
            var controller = _provider.GetRequiredService<LibraryController>();

            var result = await controller.RepairAuthorAsins(150, CancellationToken.None);

            var ok = Assert.IsType<OkObjectResult>(result);
            var payload = Assert.IsType<LibraryMaintenanceWorkflow.AuthorAsinAuditResponse>(ok.Value);
            Assert.Equal(1, payload.Repaired);

            var repaired = await _audiobookRepository.GetByIdAsync(stale.Id);
            Assert.NotNull(repaired);
            Assert.Equal(new List<string> { FondaLeeAsin }, repaired!.AuthorAsins);

            var untouched = await _audiobookRepository.GetByIdAsync(fine.Id);
            Assert.NotNull(untouched);
            Assert.Equal(new List<string> { PattersonAsin }, untouched!.AuthorAsins);

            var audit = Assert.IsType<OkObjectResult>(await controller.AuditAuthorAsins(150, CancellationToken.None));
            var after = Assert.IsType<LibraryMaintenanceWorkflow.AuthorAsinAuditResponse>(audit.Value);
            Assert.Empty(after.StaleBooks);
        }
    }
}
