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

using Listenarr.Application.Downloads;
using Microsoft.AspNetCore.Mvc;

namespace Listenarr.Api.Features.Downloads;

public partial class DownloadsController
{
    /// <summary>
    /// Activity summary: category count chips plus a collapsed-per-book download list.
    /// Terminal buckets (Imported/Failed/Stalled) are windowed; InProgress/Blocked are not.
    /// The default (no-category) view lists only in-progress items.
    /// </summary>
    /// <param name="category">Optional category to filter the list to (InProgress, Blocked, Imported, Failed, Stalled).</param>
    /// <param name="windowHours">Window for the terminal buckets. Defaults to 24.</param>
    /// <param name="page">1-based page index for the list.</param>
    /// <param name="pageSize">Page size for the list.</param>
    [HttpGet("activity")]
    public async Task<ActionResult<ActivityResponse>> GetActivity(
        [FromQuery] string? category = null,
        [FromQuery] int windowHours = 24,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 100)
    {
        try
        {
            var downloadClients = await _configurationService.GetDownloadClientConfigurationsAsync();
            var enabledClientIds = downloadClients
                .Where(c => c.IsEnabled && !string.IsNullOrWhiteSpace(c.Id))
                .Select(c => c.Id)
                .ToHashSet();
            var clientLookup = downloadClients.ToDictionary(c => c.Id, c => c.Name);
            var clientTypeLookup = downloadClients.ToDictionary(c => c.Id, c => c.Type);

            var all = await _downloadRepository.GetAllAsync();
            var inScope = all.Where(d =>
                d.DownloadClientId == "DDL" ||
                (!string.IsNullOrEmpty(d.DownloadClientId) && enabledClientIds.Contains(d.DownloadClientId)));

            ActivityCategory? filterCategory = null;
            if (!string.IsNullOrWhiteSpace(category) &&
                Enum.TryParse<ActivityCategory>(category, ignoreCase: true, out var parsed))
            {
                filterCategory = parsed;
            }

            var response = ActivitySummary.Build(
                inScope,
                DateTime.UtcNow,
                windowHours,
                filterCategory,
                page,
                pageSize,
                clientId => clientLookup.TryGetValue(clientId, out var name) ? name : "Unknown Client",
                clientId => clientTypeLookup.TryGetValue(clientId, out var type) ? type : null);

            return Ok(response);
        }
        catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            _logger.LogError(ex, "Error building activity summary");
            return StatusCode(500, new { error = "Failed to retrieve activity", message = ex.Message });
        }
    }
}
