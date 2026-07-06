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

namespace Listenarr.Api.Features.Metadata
{
    // Series disambiguation endpoints backing the "Wrong series?" picker.
    public partial class MetadataController
    {
        /// <summary>
        /// Returns candidate series for a name so the user can correct a wrong or ambiguous
        /// resolution (e.g. a mistyped/mis-parsed stored series name). Owned-book-derived
        /// candidates rank first.
        /// </summary>
        [HttpGet("series/candidates")]
        [ProducesResponseType(typeof(SeriesCandidatesResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<SeriesCandidatesResponse>> GetSeriesCandidates(
            [FromQuery] string name,
            [FromQuery] string region = "us",
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(name)) return BadRequest("Series name is required");

                var result = await _seriesCatalogService.GetSeriesCandidatesAsync(name.Trim(), region, cancellationToken);
                return Ok(new SeriesCandidatesResponse
                {
                    Query = result.Query,
                    BestGuessAsin = result.BestGuessAsin,
                    Candidates = result.Candidates.Select(candidate => new SeriesCandidateItem
                    {
                        Asin = candidate.Asin,
                        Name = candidate.Name,
                        Image = candidate.Image,
                        BookCount = candidate.BookCount,
                        Source = candidate.Source,
                        OwnedMatchCount = candidate.OwnedMatchCount
                    }).ToList()
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error fetching series candidates for {Name}", name);
                return StatusCode(500, "Internal server error");
            }
        }

        /// <summary>
        /// Resolves and persists the user's explicitly-chosen series for a name, overwriting any
        /// prior (possibly wrong) resolution for that name so the choice sticks on later loads.
        /// </summary>
        [HttpPost("series/select")]
        [ProducesResponseType(typeof(SeriesCatalogResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status500InternalServerError)]
        public async Task<ActionResult<SeriesCatalogResponse>> SelectSeries(
            [FromBody] SeriesSelectRequest? request,
            CancellationToken cancellationToken = default)
        {
            try
            {
                if (request == null || string.IsNullOrWhiteSpace(request.Name)) return BadRequest("Series name is required");
                if (string.IsNullOrWhiteSpace(request.Asin)) return BadRequest("Series ASIN is required");

                var normalizedName = request.Name.Trim();
                var catalog = await _seriesCatalogService.GetCatalogByAsinAsync(
                    normalizedName,
                    request.Asin.Trim(),
                    string.IsNullOrWhiteSpace(request.Region) ? "us" : request.Region,
                    request.Limit,
                    cancellationToken: cancellationToken);

                if (catalog == null || string.IsNullOrWhiteSpace(catalog.Series.Asin))
                {
                    return NotFound("Series not found");
                }

                return Ok(new SeriesCatalogResponse
                {
                    Series = new SeriesCatalogInfo
                    {
                        Asin = catalog.Series.Asin,
                        Name = string.IsNullOrWhiteSpace(catalog.Series.Name) ? normalizedName : catalog.Series.Name,
                        Image = catalog.Series.Image,
                        Description = catalog.Series.Description
                    },
                    Books = catalog.Books.Select(MetadataResponseMapper.MapSeriesCatalogBook).ToList(),
                    TotalBooks = catalog.TotalBooks
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Error selecting series {Asin} for {Name}", request?.Asin, request?.Name);
                return StatusCode(500, "Internal server error");
            }
        }

        public sealed class SeriesCandidatesResponse
        {
            public string Query { get; set; } = string.Empty;
            public string? BestGuessAsin { get; set; }
            public List<SeriesCandidateItem> Candidates { get; set; } = new();
        }

        public sealed class SeriesCandidateItem
        {
            public string Asin { get; set; } = string.Empty;
            public string? Name { get; set; }
            public string? Image { get; set; }
            public int? BookCount { get; set; }
            public string Source { get; set; } = "audible";
            public int OwnedMatchCount { get; set; }
        }

        public sealed class SeriesSelectRequest
        {
            public string Name { get; set; } = string.Empty;
            public string Asin { get; set; } = string.Empty;
            public string? Region { get; set; }
            public int Limit { get; set; } = 250;
        }
    }
}
