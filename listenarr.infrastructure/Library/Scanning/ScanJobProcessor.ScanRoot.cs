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
using Listenarr.Domain.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Listenarr.Infrastructure.Library.Scanning
{
    public partial class ScanJobProcessor
    {
        /// <summary>
        /// Decides what folder a scan job walks.
        /// <para>
        /// An EXPLICIT job path wins — it is the documented contract of
        /// POST /library/{id}/scan { path } ("scan a specific folder") and
        /// the operator escape hatch for reclaiming files that live OUTSIDE
        /// the record's folder. Regression fixed here: the processor used to
        /// override the explicit path with BasePath "for safety", which
        /// silently turned every such reclaim into a no-op against the
        /// record's own (possibly wrong or empty) folder — live case: 139
        /// orphan-folder reclaim scans that all "completed" without
        /// importing a single file.
        /// </para>
        /// <para>
        /// With no explicit path the record's own BasePath is scanned; the
        /// global OutputPath is the last resort only when the record has no
        /// BasePath at all (it may be large and unrelated).
        /// </para>
        /// </summary>
        private async Task<(string? ScanRoot, bool UsedBasePath)> ResolveScanRootAsync(
            ScanJob job, Audiobook audiobook, IServiceScope scope)
        {
            if (!string.IsNullOrEmpty(job.Path))
            {
                _logger.LogInformation(
                    "Using explicit requested scan root for job {JobId}: {ScanRoot}",
                    job.Id, LogRedaction.SanitizeFilePath(job.Path));
                return (job.Path, false);
            }

            if (!string.IsNullOrEmpty(audiobook.BasePath))
            {
                _logger.LogDebug(
                    "Using audiobook BasePath as scan root for job {JobId}: {ScanRoot}",
                    job.Id, audiobook.BasePath);
                return (audiobook.BasePath, true);
            }

            try
            {
                var configService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
                var settings = await configService.GetApplicationSettingsAsync();
                return (settings.OutputPath, false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogWarning(ex, "Failed to read settings for scan job {JobId}", job.Id);
                return (null, false);
            }
        }

        /// <summary>
        /// True when <paramref name="scanRoot"/> is this record's private
        /// territory: not the library output root, and no OTHER record's
        /// BasePath sits at, under, or above it. In that case the name filter
        /// (filename must contain the title) is counterproductive — the caller
        /// pointed the scan at THIS book's own folder, so every audio file in
        /// it belongs to the book regardless of what a renamer called it
        /// (live case: files named "…Volume 1-111.mp3" in a "…Volume 5"
        /// record's folder were unmatchable and the record stranded at 0
        /// files). Shared folders keep the filter — it is the shelf-hijack
        /// guard (#766).
        /// </summary>
        private async Task<bool> ScanRootBelongsExclusivelyToAsync(
            string scanRoot, Audiobook audiobook, IServiceScope scope)
        {
            try
            {
                var configService = scope.ServiceProvider.GetRequiredService<IConfigurationService>();
                var settings = await configService.GetApplicationSettingsAsync();
                var normalizedRoot = Path.GetFullPath(scanRoot);
                if (!string.IsNullOrWhiteSpace(settings.OutputPath)
                    && string.Equals(
                        normalizedRoot.TrimEnd(Path.DirectorySeparatorChar),
                        Path.GetFullPath(settings.OutputPath).TrimEnd(Path.DirectorySeparatorChar),
                        StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                var audiobookRepository = scope.ServiceProvider.GetRequiredService<IAudiobookRepository>();
                var all = await audiobookRepository.GetAllAsync();
                return !all.Any(other =>
                    other.Id != audiobook.Id
                    && !string.IsNullOrWhiteSpace(other.BasePath)
                    && (FileUtils.IsPathSameOrInside(Path.GetFullPath(other.BasePath!), normalizedRoot)
                        || FileUtils.IsPathSameOrInside(normalizedRoot, Path.GetFullPath(other.BasePath!))));
            }
            catch (Exception ex) when (ex is not OperationCanceledException && ex is not OutOfMemoryException && ex is not StackOverflowException)
            {
                _logger.LogDebug(ex, "Exclusive-ownership check failed for scan root; keeping the name filter");
                return false;
            }
        }
    }
}
