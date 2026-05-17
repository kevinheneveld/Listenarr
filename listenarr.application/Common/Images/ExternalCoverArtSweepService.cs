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

using System.Diagnostics;
using Listenarr.Application.Interfaces;
using Listenarr.Application.Interfaces.Repositories;
using Listenarr.Domain.Models;
using Microsoft.Extensions.Logging;

namespace Listenarr.Application.Common.Images
{
    public interface IExternalCoverArtSweepService
    {
        Task<ExternalCoverArtSweepResult> SweepAsync(CancellationToken cancellationToken = default);
    }

    public sealed class ExternalCoverArtSweepResult
    {
        public int TotalScanned { get; init; }
        public int AlreadyLocal { get; init; }
        public int Queued { get; init; }
        public int Succeeded { get; init; }
        public int Failed { get; init; }
        public long DurationMs { get; init; }
    }

    /// <summary>
    /// Admin-triggered, one-shot sweep that walks every audiobook and downloads
    /// any external http(s) cover art into local library storage. Idempotent —
    /// re-running after a successful sweep is a no-op because every record will
    /// already point at a local cache path.
    /// </summary>
    public sealed class ExternalCoverArtSweepService : IExternalCoverArtSweepService
    {
        // Be polite to the Amazon CDN. There's no shared throttle utility in the
        // codebase — `Task.Delay` between successful downloads is the existing
        // pattern (see DownloadService.cs:740, FfmpegService.cs:77, etc.).
        // 250ms ≈ 4 downloads/sec, plenty for a typical few-hundred-book library
        // and well under what an unauthenticated CDN endpoint should care about.
        private const int InterDownloadDelayMs = 250;

        private readonly IAudiobookRepository _repository;
        private readonly IImageCacheService _imageCacheService;
        private readonly ILogger<ExternalCoverArtSweepService> _logger;

        public ExternalCoverArtSweepService(
            IAudiobookRepository repository,
            IImageCacheService imageCacheService,
            ILogger<ExternalCoverArtSweepService> logger)
        {
            _repository = repository;
            _imageCacheService = imageCacheService;
            _logger = logger;
        }

        public async Task<ExternalCoverArtSweepResult> SweepAsync(CancellationToken cancellationToken = default)
        {
            var stopwatch = Stopwatch.StartNew();
            var audiobooks = await _repository.GetAllAsync();

            var totalScanned = audiobooks.Count;
            var alreadyLocal = 0;
            var queued = 0;
            var succeeded = 0;
            var failed = 0;

            _logger.LogInformation(
                "Starting external cover-art sweep across {Total} audiobooks", totalScanned);

            foreach (var audiobook in audiobooks)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var url = audiobook.ImageUrl;
                if (!ShouldCacheLocally(url))
                {
                    alreadyLocal++;
                    continue;
                }

                queued++;

                string? movedPath = null;
                try
                {
                    movedPath = await LibraryImageStorageHelper.MoveExternalImageToLibraryAsync(
                        _imageCacheService,
                        audiobook,
                        url!,
                        _logger);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Per-record failure must not abort the sweep. The helper
                    // catches the common HTTP/IO/timeout exceptions itself; this
                    // outer catch is a belt-and-suspenders for anything novel.
                    _logger.LogWarning(ex,
                        "Unexpected failure caching cover for audiobook {AudiobookId}", audiobook.Id);
                }

                if (string.IsNullOrWhiteSpace(movedPath))
                {
                    // Leave the external URL in place — matches the on-save
                    // hook's fallback behavior and avoids a needless write.
                    failed++;
                    _logger.LogWarning(
                        "Cover sweep: download failed for audiobook {AudiobookId}, keeping external URL",
                        audiobook.Id);
                    continue;
                }

                audiobook.ImageUrl = movedPath;
                try
                {
                    await _repository.UpdateAsync(audiobook);
                    succeeded++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    failed++;
                    _logger.LogWarning(ex,
                        "Cover sweep: persisted cache file but failed to update audiobook {AudiobookId}",
                        audiobook.Id);
                    continue;
                }

                // Only sleep when we actually hit the CDN — no point throttling
                // skips or local cache hits.
                try
                {
                    await Task.Delay(InterDownloadDelayMs, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            stopwatch.Stop();

            _logger.LogInformation(
                "External cover-art sweep finished: scanned={Total} alreadyLocal={Local} queued={Queued} succeeded={Succeeded} failed={Failed} durationMs={Duration}",
                totalScanned, alreadyLocal, queued, succeeded, failed, stopwatch.ElapsedMilliseconds);

            return new ExternalCoverArtSweepResult
            {
                TotalScanned = totalScanned,
                AlreadyLocal = alreadyLocal,
                Queued = queued,
                Succeeded = succeeded,
                Failed = failed,
                DurationMs = stopwatch.ElapsedMilliseconds,
            };
        }

        /// <summary>
        /// Whether this audiobook's ImageUrl looks like an external URL we
        /// haven't already cached. Stricter than <see cref="LibraryImageStorageHelper.IsExternalHttpImageUrl"/>
        /// because the sweep also has to skip fully-qualified URLs that happen
        /// to point back at our own local cache routes — e.g. a record carrying
        /// "https://listenarr.example.com/api/v1/images/B00..." or a legacy
        /// "https://.../config/cache/...". The on-save hook can't be widened in
        /// the same way without changing its contract.
        /// </summary>
        private static bool ShouldCacheLocally(string? url)
        {
            if (!LibraryImageStorageHelper.IsExternalHttpImageUrl(url)) return false;

            // Cheap substring checks against the known local-image route fragments.
            // Avoids parsing a Uri on every record; the fragments are distinctive
            // enough that a false-positive substring match in a real CDN URL is
            // implausible.
            if (url!.Contains("/api/v1/images/", StringComparison.OrdinalIgnoreCase)) return false;
            if (url.Contains("/config/cache/", StringComparison.OrdinalIgnoreCase)) return false;
            if (url.Contains("/cache/images/", StringComparison.OrdinalIgnoreCase)) return false;

            return true;
        }
    }
}
