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

using Microsoft.AspNetCore.Mvc;
using Listenarr.Tests.Builders;
using Listenarr.Tests.Common;

namespace Listenarr.Tests.Features.Api.Features.Library
{
    /// <summary>
    /// "Series you're not collecting": owned-but-unmonitored series are candidates;
    /// monitored or fileless ones are not; a dismissal hides a series until undone.
    /// </summary>
    [Trait("Area", "LibraryApi")]
    [Trait("Name", "LibrarySeriesTriageWorkflowTests")]
    [Trait("Category", "Series")]
    public class LibrarySeriesTriageWorkflowTests : BaseTests
    {
        private async Task<Audiobook> AddBookAsync(string title, string series, bool owned, string author = "Some Author", string position = "1")
        {
            // Positions keep distinct works distinct under the health workflow's
            // work-keying (same rule the dashboard's series-health tests follow).
            var book = await _audiobookRepository.AddAsync(new AudiobookBuilder()
                .WithTitle(title)
                .WithAuthor(author)
                .WithSeries(series)
                .WithSeriesNumber(position)
                .WithBasePath(FileService.GetTempPath())
                .Build());
            if (owned)
            {
                await _audiobookFileRepository.AddAsync(new AudiobookFileBuilder()
                    .WithAudiobook(book)
                    .WithPath(Path.Join(FileService.GetTempPath(), $"{title}.m4b"))
                    .Build());
            }
            return book;
        }

        private async Task SeedAsync()
        {
            await AddBookAsync("Alpha 1", "Alpha", owned: true, author: "Alice Author", position: "1");
            await AddBookAsync("Alpha 2", "Alpha", owned: true, author: "Alice Author", position: "2");
            await AddBookAsync("Beta 1", "Beta", owned: false);
            await AddBookAsync("Gamma 1", "Gamma", owned: true);
            await _provider.GetRequiredService<IMonitoredSeriesRepository>().UpsertAsync(new MonitoredSeries
            {
                SeriesName = "Gamma",
                SeriesNameNormalized = "gamma",
                Region = "us",
                Language = "all"
            });
        }

        private LibrarySeriesTriageWorkflow Workflow() => _provider.GetRequiredService<LibrarySeriesTriageWorkflow>();

        private static LibrarySeriesTriageWorkflow.SeriesTriageDecisionRequest Request(string name) =>
            new() { SeriesName = name };

        [Fact]
        public async Task Get_ReturnsOnlyOwnedUnmonitoredSeries()
        {
            await SeedAsync();

            var response = await Workflow().GetAsync(includeDismissed: false, CancellationToken.None);

            var row = Assert.Single(response.Rows);
            Assert.Equal("Alpha", row.Name);
            Assert.Equal(2, row.Owned);
            Assert.Equal(new[] { "Alice Author" }, row.Authors);
            Assert.False(row.Dismissed);
            Assert.Equal(1, response.Summary.Candidates);
            Assert.Equal(2, response.Summary.OwnedBooksInCandidates);
            Assert.Equal(0, response.Summary.SingleBook);
            Assert.Equal(0, response.Summary.Dismissed);
        }

        [Fact]
        public async Task Dismiss_HidesSeries_UntilIncludeDismissedOrUndismiss()
        {
            await SeedAsync();
            var workflow = Workflow();

            var dismissed = await workflow.DismissAsync(Request("Alpha"), CancellationToken.None);
            Assert.IsType<OkObjectResult>(dismissed);

            var hidden = await workflow.GetAsync(includeDismissed: false, CancellationToken.None);
            Assert.Empty(hidden.Rows);
            Assert.Equal(0, hidden.Summary.Candidates);
            Assert.Equal(1, hidden.Summary.Dismissed);

            var shown = await workflow.GetAsync(includeDismissed: true, CancellationToken.None);
            var row = Assert.Single(shown.Rows);
            Assert.True(row.Dismissed);
            Assert.NotNull(row.DismissedAt);

            var undone = Assert.IsType<OkObjectResult>(await workflow.UndismissAsync(Request("alpha"), CancellationToken.None));
            Assert.NotNull(undone.Value);

            var back = await workflow.GetAsync(includeDismissed: false, CancellationToken.None);
            Assert.Equal("Alpha", Assert.Single(back.Rows).Name);
        }

        [Fact]
        public async Task Dismiss_BlankName_IsBadRequest()
        {
            var result = await Workflow().DismissAsync(Request("   "), CancellationToken.None);

            Assert.IsType<BadRequestObjectResult>(result);
        }

        [Fact]
        public async Task Controller_Endpoints_RouteToWorkflow()
        {
            await SeedAsync();
            var controller = _provider.GetRequiredService<LibraryController>();

            var get = await controller.GetSeriesTriage(includeDismissed: false, CancellationToken.None);
            var ok = Assert.IsType<OkObjectResult>(get.Result);
            var payload = Assert.IsType<LibrarySeriesTriageWorkflow.SeriesTriageResponse>(ok.Value);
            Assert.Equal("Alpha", Assert.Single(payload.Rows).Name);

            Assert.IsType<OkObjectResult>(await controller.DismissSeriesTriage(Request("Alpha"), CancellationToken.None));
            var after = Assert.IsType<OkObjectResult>((await controller.GetSeriesTriage(false, CancellationToken.None)).Result);
            Assert.Empty(Assert.IsType<LibrarySeriesTriageWorkflow.SeriesTriageResponse>(after.Value).Rows);

            Assert.IsType<OkObjectResult>(await controller.UndismissSeriesTriage(Request("Alpha"), CancellationToken.None));
            Assert.IsType<BadRequestObjectResult>(await controller.DismissSeriesTriage(Request(""), CancellationToken.None));
        }
    }
}
