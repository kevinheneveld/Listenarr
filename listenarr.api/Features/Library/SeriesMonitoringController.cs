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

namespace Listenarr.Api.Features.Library
{
    [ApiController]
    [Route("api/v{version:apiVersion}/series/monitoring")]
    [Tags("Series")]
    public class SeriesMonitoringController : ControllerBase
    {
        private readonly ISeriesMonitoringService _seriesMonitoringService;
        private readonly ILogger<SeriesMonitoringController> _logger;

        public SeriesMonitoringController(
            ISeriesMonitoringService seriesMonitoringService,
            ILogger<SeriesMonitoringController> logger)
        {
            _seriesMonitoringService = seriesMonitoringService;
            _logger = logger;
        }

        [HttpGet("status")]
        [ProducesResponseType(typeof(SeriesMonitoringStatusResponse), StatusCodes.Status200OK)]
        public async Task<ActionResult<SeriesMonitoringStatusResponse>> GetStatus(
            [FromQuery] string name,
            [FromQuery] string region = "us",
            [FromQuery] string language = "all",
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return BadRequest("Series name is required");
            }

            try
            {
                var monitoredSeries = await _seriesMonitoringService.GetMonitoredSeriesAsync(
                    name,
                    region,
                    language,
                    cancellationToken);

                return Ok(new SeriesMonitoringStatusResponse
                {
                    IsMonitored = monitoredSeries != null,
                    MonitoredSeries = monitoredSeries == null ? null : ToResponse(monitoredSeries)
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Failed to get series monitoring status for {Series}", name);
                return StatusCode(StatusCodes.Status500InternalServerError, "Internal server error");
            }
        }

        [HttpPost]
        [ProducesResponseType(typeof(MonitorSeriesResponse), StatusCodes.Status200OK)]
        public async Task<ActionResult<MonitorSeriesResponse>> MonitorSeries(
            [FromBody] MonitorSeriesRequest request,
            CancellationToken cancellationToken = default)
        {
            try
            {
                var result = await _seriesMonitoringService.MonitorSeriesAsync(request, cancellationToken);
                if (result.MonitoredSeries == null)
                {
                    return StatusCode(StatusCodes.Status500InternalServerError, "Failed to monitor series");
                }

                return Ok(new MonitorSeriesResponse
                {
                    Message = "Series monitoring enabled",
                    MonitoredSeries = ToResponse(result.MonitoredSeries),
                    AddedCount = result.SyncResult.AddedCount,
                    ExistingCount = result.SyncResult.ExistingCount,
                    FailedCount = result.SyncResult.FailedCount,
                    ErrorMessage = result.SyncResult.ErrorMessage
                });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Failed to enable monitoring for series {Series}", request?.Name);
                return StatusCode(StatusCodes.Status500InternalServerError, "Internal server error");
            }
        }

        /// <summary>
        /// Pin an explicit series ASIN onto a monitored series and re-sync using it —
        /// the "Wrong series?" correction. Collapses into an existing monitored series
        /// when the ASIN is already tracked (Merged = true).
        /// </summary>
        [HttpPut("{id:int}")]
        [ProducesResponseType(typeof(MonitorSeriesResponse), StatusCodes.Status200OK)]
        public async Task<ActionResult<MonitorSeriesResponse>> RepointSeries(
            int id,
            [FromBody] RepointSeriesRequest request,
            CancellationToken cancellationToken = default)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.Asin))
            {
                return BadRequest("A series ASIN is required.");
            }

            try
            {
                var result = await _seriesMonitoringService.RepointSeriesAsync(id, request.Asin, cancellationToken);
                if (result?.MonitoredSeries == null)
                {
                    return NotFound();
                }

                return Ok(new MonitorSeriesResponse
                {
                    Message = result.Merged
                        ? "Series merged into the existing monitored series for that ASIN"
                        : "Series re-pointed",
                    Merged = result.Merged,
                    MonitoredSeries = ToResponse(result.MonitoredSeries),
                    AddedCount = result.SyncResult.AddedCount,
                    ExistingCount = result.SyncResult.ExistingCount,
                    FailedCount = result.SyncResult.FailedCount,
                    ErrorMessage = result.SyncResult.ErrorMessage
                });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Failed to re-point monitored series {SeriesId}", id);
                return StatusCode(StatusCodes.Status500InternalServerError, "Internal server error");
            }
        }

        [HttpDelete("{id:int}")]
        public async Task<IActionResult> UnmonitorSeries(int id, CancellationToken cancellationToken = default)
        {
            try
            {
                var removed = await _seriesMonitoringService.UnmonitorSeriesAsync(id, cancellationToken);
                if (!removed)
                {
                    return NotFound();
                }

                return Ok(new { message = "Series monitoring disabled" });
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogError(ex, "Failed to disable monitoring for series {SeriesId}", id);
                return StatusCode(StatusCodes.Status500InternalServerError, "Internal server error");
            }
        }

        private static MonitoredSeriesResponse ToResponse(MonitoredSeries monitoredSeries)
        {
            return new MonitoredSeriesResponse
            {
                Id = monitoredSeries.Id,
                SeriesName = monitoredSeries.SeriesName,
                SeriesAsin = monitoredSeries.SeriesAsin,
                AsinPinned = monitoredSeries.AsinPinned,
                Region = monitoredSeries.Region,
                Language = monitoredSeries.Language,
                CreatedAt = monitoredSeries.CreatedAt,
                UpdatedAt = monitoredSeries.UpdatedAt,
                LastCheckedAt = monitoredSeries.LastCheckedAt,
                LastSuccessfulSyncAt = monitoredSeries.LastSuccessfulSyncAt,
                LastError = monitoredSeries.LastError
            };
        }

        public sealed class SeriesMonitoringStatusResponse
        {
            public bool IsMonitored { get; set; }

            public MonitoredSeriesResponse? MonitoredSeries { get; set; }
        }

        public sealed class RepointSeriesRequest
        {
            public string Asin { get; set; } = string.Empty;
        }

        public sealed class MonitorSeriesResponse
        {
            public string Message { get; set; } = string.Empty;

            public bool Merged { get; set; }

            public MonitoredSeriesResponse MonitoredSeries { get; set; } = new();

            public int AddedCount { get; set; }

            public int ExistingCount { get; set; }

            public int FailedCount { get; set; }

            public string? ErrorMessage { get; set; }
        }

        public sealed class MonitoredSeriesResponse
        {
            public int Id { get; set; }

            public string SeriesName { get; set; } = string.Empty;

            public string? SeriesAsin { get; set; }

            public bool AsinPinned { get; set; }

            public string Region { get; set; } = "us";

            public string Language { get; set; } = "all";

            public DateTime CreatedAt { get; set; }

            public DateTime UpdatedAt { get; set; }

            public DateTime? LastCheckedAt { get; set; }

            public DateTime? LastSuccessfulSyncAt { get; set; }

            public string? LastError { get; set; }
        }
    }
}
