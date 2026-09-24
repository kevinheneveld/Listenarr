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
    /// Relabelling a wrong grab rewrites Authors; the record's AuthorAsins must follow,
    /// or the author page resolves the new name to the old author (live: "Cross Fire"
    /// relabelled to Fonda Lee kept James Patterson's ASIN).
    /// </summary>
    [Trait("Name", "LibraryUpdateWorkflow_AuthorAsinsTests")]
    [Trait("Category", "Library")]
    public class LibraryUpdateWorkflow_AuthorAsinsTests : BaseTests
    {
        private const string PattersonAsin = "B000APZGGS";
        private const string FondaLeeAsin = "B0FONDA001";

        // Built by hand (like LibraryUpdateWorkflow_AsinConflictTests) around the
        // test container's repository, with a stubbed Audible lookup feeding the
        // resolver through the same scope the workflow uses.
        private LibraryUpdateWorkflow CreateWorkflow()
        {
            using var httpClient = new HttpClient();
            var audible = new Mock<AudibleService>(httpClient, NullLogger<AudibleService>.Instance);
            audible
                .Setup(service => service.LookupAuthorAsync("Fonda Lee", It.IsAny<string>()))
                .ReturnsAsync(new AuthorLookupItem { Asin = FondaLeeAsin, Name = "Fonda Lee" });
            audible
                .Setup(service => service.LookupAuthorAsync(It.Is<string>(name => name != "Fonda Lee"), It.IsAny<string>()))
                .ReturnsAsync((AuthorLookupItem?)null);

            var services = new ServiceCollection();
            services.AddSingleton(_audiobookRepository);
            services.AddSingleton(audible.Object);
            var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

            return new LibraryUpdateWorkflow(
                scopeFactory,
                new Mock<IAudiobookDestinationRewriteService>(MockBehavior.Loose).Object,
                new AudiobookOperationCoordinator(),
                new FileSystemSemanticsResolver(),
                NullLogger<LibraryUpdateWorkflow>.Instance,
                new AuthorAsinResolver(scopeFactory, NullLogger<AuthorAsinResolver>.Instance));
        }

        private async Task<Audiobook> SeedPattersonBookAsync()
        {
            return await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Cross Fire")
                .WithAuthor("James Patterson")
                .WithAuthorAsin(PattersonAsin)
                .Build());
        }

        [Fact]
        public async Task UpdateAsync_AuthorSetChangesWithoutAsins_DropsStaleAsinAndReResolves()
        {
            var book = await SeedPattersonBookAsync();
            var workflow = CreateWorkflow();

            var result = await workflow.UpdateAsync(book.Id, new AudiobookUpdateRequest
            {
                Title = "Cross Fire",
                Authors = new List<string> { "Fonda Lee" }
            });

            Assert.IsType<OkObjectResult>(result);
            var updated = await _audiobookRepository.GetByIdAsync(book.Id);
            Assert.NotNull(updated);
            Assert.Equal(new List<string> { "Fonda Lee" }, updated!.Authors);
            Assert.Equal(new List<string> { FondaLeeAsin }, updated.AuthorAsins);
        }

        [Fact]
        public async Task UpdateAsync_SuppliedAuthorAsins_AreUsedVerbatim()
        {
            var book = await SeedPattersonBookAsync();
            var workflow = CreateWorkflow();

            var result = await workflow.UpdateAsync(book.Id, new AudiobookUpdateRequest
            {
                Authors = new List<string> { "Fonda Lee" },
                AuthorAsins = new List<string> { " B0EXPLICIT1 ", "B0EXPLICIT1", "" }
            });

            Assert.IsType<OkObjectResult>(result);
            var updated = await _audiobookRepository.GetByIdAsync(book.Id);
            Assert.NotNull(updated);
            Assert.Equal(new List<string> { "B0EXPLICIT1" }, updated!.AuthorAsins);
        }

        [Fact]
        public async Task UpdateAsync_SameAuthorSet_LeavesAuthorAsinsAlone()
        {
            var book = await SeedPattersonBookAsync();
            var workflow = CreateWorkflow();

            var result = await workflow.UpdateAsync(book.Id, new AudiobookUpdateRequest
            {
                Title = "Cross Fire (Alex Cross)",
                Authors = new List<string> { "james patterson" }
            });

            Assert.IsType<OkObjectResult>(result);
            var updated = await _audiobookRepository.GetByIdAsync(book.Id);
            Assert.NotNull(updated);
            Assert.Equal(new List<string> { PattersonAsin }, updated!.AuthorAsins);
        }

        [Fact]
        public async Task UpdateAsync_AuthorLookupFails_LeavesNoStaleAsinBehind()
        {
            var book = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle("Some Book")
                .WithAuthor("James Patterson")
                .WithAuthorAsin(PattersonAsin)
                .Build());
            var workflow = CreateWorkflow();

            var result = await workflow.UpdateAsync(book.Id, new AudiobookUpdateRequest
            {
                Authors = new List<string> { "Unknown Person" }
            });

            Assert.IsType<OkObjectResult>(result);
            var updated = await _audiobookRepository.GetByIdAsync(book.Id);
            Assert.NotNull(updated);
            Assert.Empty(updated!.AuthorAsins ?? new List<string>());
        }
    }
}
